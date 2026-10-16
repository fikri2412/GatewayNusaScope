using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

public class QueuePolicyTests : QueueTestBase
{
    [Fact]
    public async Task Retry_limit_is_pinned_to_each_row_not_the_repository_or_latest_settings()
    {
        var repo = NewRepo();
        var once = await EnqueueAsync(await NewConversationAsync(), policy: Policy(maxRetries: 1));
        var three = await EnqueueAsync(await NewConversationAsync(), policy: Policy(maxRetries: 3));
        await repo.ClaimQueueItemAsync("once");
        Assert.Equal(Statuses.Failed, (await repo.FailQueueItemAsync(once, "once", "bad"))!.Status);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var run = $"three-{attempt}";
            await repo.ClaimQueueItemAsync(run);
            Assert.Equal(attempt == 3 ? Statuses.Failed : Statuses.Pending,
                (await repo.FailQueueItemAsync(three, run, "bad"))!.Status);
        }
        Assert.Equal(3, (await GetItemAsync(three)).RetryCount);
    }

    [Fact]
    public async Task Stale_recovery_uses_each_rows_original_timeout()
    {
        var repo = NewRepo();
        var shortLease = await EnqueueAsync(await NewConversationAsync(), policy: Policy(staleLockMinutes: 1));
        var longLease = await EnqueueAsync(await NewConversationAsync(), policy: Policy(staleLockMinutes: 20));
        await repo.ClaimQueueItemAsync("short");
        await repo.ClaimQueueItemAsync("long");
        await AgeLockAsync(shortLease, 5);
        await AgeLockAsync(longLease, 5);
        Assert.Equal(1, (await repo.RecoverStaleAsync()).Recovered);
        Assert.Equal(Statuses.Pending, (await GetItemAsync(shortLease)).Status);
        Assert.Equal((Statuses.Processing, "long", 0),
            ((await GetItemAsync(longLease)).Status, (await GetItemAsync(longLease)).LockedBy, (await GetItemAsync(longLease)).RetryCount));
    }

    [Fact]
    public async Task Heartbeat_refreshes_owned_work_and_cancels_when_another_run_takes_over()
    {
        var repo = NewRepo();
        var id = await EnqueueAsync(await NewConversationAsync(), policy: Policy(staleLockMinutes: 1));
        await repo.ClaimQueueItemAsync("old");
        await AgeLockAsync(id, 5);
        await using var lease = await repo.KeepAliveAsync(id, "old", isJob: false, CancellationToken.None);
        Assert.Equal(0, (await repo.RecoverStaleAsync()).Recovered);
        await repo.ReleaseRunAsync("old");
        await repo.ClaimQueueItemAsync("new");
        await Assert.ThrowsAsync<LeaseLostException>(() => repo.KeepAliveAsync(id, "old", false, CancellationToken.None));
        Assert.True(await Task.Run(() => lease.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(35))));
        Assert.False(await repo.CompleteQueueItemAsync(id, "old"));
        Assert.True(await repo.CompleteQueueItemAsync(id, "new"));
    }

    [Fact]
    public async Task Heartbeat_blocked_by_the_runs_own_open_transaction_means_alive_not_lost()
    {
        var repo = NewRepo();
        var id = await EnqueueAsync(await NewConversationAsync(), policy: Policy(staleLockMinutes: 1));
        await repo.ClaimQueueItemAsync("run");

        // Seperti RunToolsAsync: UPDATE guard menahan kunci X atas baris tiket sampai commit, selama tool berjalan.
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await conn.BeginTransactionAsync();
        await using (var guard = conn.CreateCommand())
        {
            guard.Transaction = tx;
            guard.CommandText = "UPDATE queue_items SET locked_at = SYSUTCDATETIME() WHERE id = @id";
            guard.Parameters.AddWithValue("@id", id);
            Assert.Equal(1, await guard.ExecuteNonQueryAsync());
        }

        // Sebelumnya: menunggu 10 detik lalu LeaseLostException, padahal tiket tidak hilang.
        await using var lease = await repo.KeepAliveAsync(id, "run", isJob: false, CancellationToken.None);

        Assert.False(lease.Token.IsCancellationRequested);
        await tx.CommitAsync();
        Assert.True(await repo.CompleteQueueItemAsync(id, "run")); // kepemilikan tetap utuh
    }

    [Fact]
    public async Task Active_rows_without_origin_or_recovery_bounds_are_rejected_by_the_database()
    {
        var conversation = await NewConversationAsync();
        foreach (var (source, json) in new (long?, string)[]
        {
            (null, Policy().ToJson()),
            (1, "{}"),
            (1, "{\"maxRetries\":3}"),
        })
        {
            await using var db = NewContext();
            db.QueueItems.Add(new QueueItem { ConversationId = conversation, Type = QueueTypes.UserMessage, PayloadJson = "{}", SourceMessageId = source, ExecutionPolicyJson = json });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Theory]
    [InlineData("ai")]
    [InlineData("upload")]
    [InlineData("document")]
    public async Task Completed_legacy_rows_remain_history_but_cannot_be_reactivated_without_runtime_limits(string section)
    {
        var id = await EnqueueAsync(await NewConversationAsync());
        await using (var db = NewContext())
        {
            var row = await db.QueueItems.SingleAsync(q => q.Id == id);
            var json = JsonNode.Parse(row.ExecutionPolicyJson!)!.AsObject();
            json.Remove(section);
            row.Status = Statuses.Done;
            row.ExecutionPolicyJson = json.ToJsonString();
            await db.SaveChangesAsync();
            row.Status = Statuses.Pending;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(Statuses.Done, (await GetItemAsync(id)).Status);
    }
}
