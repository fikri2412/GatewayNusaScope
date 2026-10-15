using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

public class QueueRecoveryTests : QueueTestBase
{
    [Fact]
    public async Task Failure_retries_until_max_then_fails_permanently()
    {
        var repo = NewRepo();
        var id = await EnqueueAsync(await NewConversationAsync(), policy: Policy(maxRetries: 2));

        await repo.ClaimQueueItemAsync("run1");
        Assert.Equal(Statuses.Pending, (await repo.FailQueueItemAsync(id, "run1", "boom 1"))!.Status);
        var afterFirst = await GetItemAsync(id);
        Assert.Equal((Statuses.Pending, 1, null), (afterFirst.Status, afterFirst.RetryCount, afterFirst.LockedBy));

        await repo.ClaimQueueItemAsync("run2");
        Assert.Equal(Statuses.Failed, (await repo.FailQueueItemAsync(id, "run2", "boom 2"))!.Status);
        var final = await GetItemAsync(id);
        Assert.Equal((Statuses.Failed, 2, "boom 2"), (final.Status, final.RetryCount, final.Error));
        Assert.Null(await repo.ClaimQueueItemAsync("run3"));
    }

    [Fact]
    public async Task Stale_lock_returns_to_pending_and_the_old_worker_can_no_longer_complete()
    {
        var repo = NewRepo();
        var id = await EnqueueAsync(await NewConversationAsync());
        await repo.ClaimQueueItemAsync("old");

        Assert.Equal(0, (await repo.RecoverStaleAsync()).Recovered);      // kunci masih segar: tidak disentuh
        await AgeLockAsync(id, minutes: 11);
        Assert.Equal(1, (await repo.RecoverStaleAsync()).Recovered);

        var recovered = await GetItemAsync(id);
        Assert.Equal((Statuses.Pending, 1, null, null), (recovered.Status, recovered.RetryCount, recovered.LockedBy, recovered.LockedAt));

        Assert.NotNull(await repo.ClaimQueueItemAsync("new"));
        Assert.False(await repo.CompleteQueueItemAsync(id, "old"));   // bukan miliknya lagi
        Assert.True(await repo.CompleteQueueItemAsync(id, "new"));
    }

    [Fact]
    public async Task Crashed_run_is_recovered_by_run_id_without_touching_other_runs()
    {
        var repo = NewRepo();
        var crashed = await EnqueueAsync(await NewConversationAsync());
        var healthy = await EnqueueAsync(await NewConversationAsync());
        await repo.ClaimQueueItemAsync("crashed");
        await repo.ClaimQueueItemAsync("healthy");

        Assert.Equal(1, (await repo.RecoverRunAsync("crashed")).Recovered);

        var recovered = await GetItemAsync(crashed);
        Assert.Equal((Statuses.Pending, 1), (recovered.Status, recovered.RetryCount));
        var other = await GetItemAsync(healthy);
        Assert.Equal((Statuses.Processing, "healthy"), (other.Status, other.LockedBy));
    }

    [Fact]
    public async Task A_stopped_run_returns_its_queue_item_and_job_to_pending_without_counting_a_retry_and_leaves_other_runs_alone()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var item = await EnqueueAsync(conversation);
        var job = await NewJobAsync(conversation);
        var otherJob = await NewJobAsync(conversation);
        await repo.ClaimQueueItemAsync("stopped");
        await repo.ClaimJobAsync("stopped", [JobTypes.ExcelRead]);
        await repo.ClaimJobAsync("healthy", [JobTypes.ExcelRead]);

        Assert.Equal(2, await repo.ReleaseRunAsync("stopped"));

        var releasedItem = await GetItemAsync(item);
        Assert.Equal((Statuses.Pending, 0, null, null), (releasedItem.Status, releasedItem.RetryCount, releasedItem.LockedBy, releasedItem.LockedAt));
        var releasedJob = await GetJobAsync(job);
        Assert.Equal((Statuses.Pending, 0, null), (releasedJob.Status, releasedJob.RetryCount, releasedJob.LockedBy));
        Assert.Equal((Statuses.Processing, "healthy"), ((await GetJobAsync(otherJob)).Status, (await GetJobAsync(otherJob)).LockedBy));
        Assert.NotNull(await repo.ClaimQueueItemAsync("next"));             // proses lain langsung bisa mengambilnya
        Assert.Equal(0, await repo.ReleaseRunAsync("stopped"));            // idempoten
    }

    [Fact]
    public async Task Item_that_keeps_crashing_ends_up_failed_with_one_apology_instead_of_looping_forever()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var id = await EnqueueAsync(conversation, policy: Policy(maxRetries: 2));

        RecoveryResult last = null!;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.NotNull(await repo.ClaimQueueItemAsync($"run{attempt}"));
            last = await repo.RecoverRunAsync($"run{attempt}");
        }

        Assert.Equal(Statuses.Failed, (await GetItemAsync(id)).Status);
        Assert.Null(await repo.ClaimQueueItemAsync("run3"));
        // Percakapan tidak boleh menggantung tanpa jawaban: permintaan maaf visible, sekali saja (bukan di percobaan pertama).
        var apology = Assert.Single(last.Apologies);
        Assert.Equal(conversation, apology.ConversationId);
        await using var db = NewContext();
        var message = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal((apology.MessageId, MessageRoles.Assistant, Replies.FailureApology, true, TestModel),
            (message.Id, message.Role, message.Content, message.IsVisible, message.ModelAlias));
    }

    [Fact]
    public async Task Retry_and_recovery_keep_the_model_of_the_row_they_belong_to()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var id = await EnqueueAsync(conversation, policy: Policy(maxRetries: 2));
        var payload = PacketJson("model-A");
        await using (var db = NewContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE queue_items SET payload_json = {0} WHERE id = {1}", payload, id);

        await repo.ClaimQueueItemAsync("run1");
        await repo.FailQueueItemAsync(id, "run1", "gangguan sementara");        // kembali Pending, payload utuh
        Assert.Equal(payload, (await GetItemAsync(id)).PayloadJson);

        await repo.ClaimQueueItemAsync("run2");
        await AgeLockAsync(id, minutes: 11);
        var recovered = await repo.RecoverStaleAsync();                         // retry habis → Failed + permintaan maaf

        Assert.Equal(Statuses.Failed, (await GetItemAsync(id)).Status);
        Assert.Equal(conversation, Assert.Single(recovered.Apologies).ConversationId);
        await using var after = NewContext();
        Assert.Equal("model-A", (await after.Messages.AsNoTracking().SingleAsync()).ModelAlias);
    }
}
