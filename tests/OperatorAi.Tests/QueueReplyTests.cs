using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

public class QueueReplyTests : QueueTestBase
{
    [Fact]
    public async Task Complete_with_reply_writes_done_and_the_visible_reply_together()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var id = await EnqueueAsync(conversation);
        await repo.ClaimQueueItemAsync("run1");

        var reply = await repo.CompleteQueueItemWithReplyAsync(id, "run1", conversation, "Halo!");

        Assert.NotNull(reply);
        Assert.Equal(Statuses.Done, (await GetItemAsync(id)).Status);
        await using var db = NewContext();
        var message = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal((reply!.Value.MessageId, conversation, MessageRoles.Assistant, "Halo!", true, TestModel),
            (message.Id, message.ConversationId, message.Role, message.Content, message.IsVisible, message.ModelAlias));
    }

    [Fact]
    public async Task Complete_with_reply_writes_nothing_when_the_ticket_is_no_longer_owned()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var id = await EnqueueAsync(conversation);
        await repo.ClaimQueueItemAsync("old");
        await AgeLockAsync(id, minutes: 11);
        await repo.RecoverStaleAsync();
        await repo.ClaimQueueItemAsync("new");

        Assert.Null(await repo.CompleteQueueItemWithReplyAsync(id, "old", conversation, "terlambat"));

        await using var db = NewContext();
        Assert.Empty(db.Messages);                       // tidak ada balasan ganda
        Assert.Equal(Statuses.Processing, (await GetItemAsync(id)).Status);
    }

    [Fact]
    public async Task Retryable_failure_writes_no_apology_but_the_final_failure_writes_exactly_one()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var id = await EnqueueAsync(conversation, policy: Policy(maxRetries: 2));

        await repo.ClaimQueueItemAsync("run1");
        var first = await repo.FailQueueItemAsync(id, "run1", "AI mati");
        Assert.Equal((Statuses.Pending, null), (first!.Status, first.Apology));
        await using (var db = NewContext()) Assert.Empty(db.Messages);

        await repo.ClaimQueueItemAsync("run2");
        var second = await repo.FailQueueItemAsync(id, "run2", "AI mati");
        Assert.Equal(Statuses.Failed, second!.Status);
        Assert.Equal(conversation, second.Apology!.Value.ConversationId);

        await using var after = NewContext();
        var message = await after.Messages.AsNoTracking().SingleAsync();
        Assert.Equal((second.Apology.Value.MessageId, Replies.FailureApology, true, TestModel),
            (message.Id, message.Content, message.IsVisible, message.ModelAlias));
    }

    [Fact]
    public async Task Failed_non_user_tickets_get_no_apology()
    {
        var repo = NewRepo();
        var id = await EnqueueAsync(await NewConversationAsync(), QueueTypes.JobFailed, policy: Policy(maxRetries: 1));
        await repo.ClaimQueueItemAsync("run1");

        var result = await repo.FailQueueItemAsync(id, "run1", "x");

        Assert.Equal((Statuses.Failed, null), (result!.Status, result.Apology));
        await using var db = NewContext();
        Assert.Empty(db.Messages);
    }

    [Fact]
    public async Task Terminal_failure_stops_at_the_first_attempt_and_always_answers_the_user()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var id = await EnqueueAsync(conversation, policy: Policy(maxRetries: 3));
        await repo.ClaimQueueItemAsync("run1");

        var result = await repo.FailQueueItemTerminallyAsync(id, "run1", "model dicabut", Replies.ModelUnavailable);

        // Sisa retry masih ada, tetapi model yang dicabut tidak akan berubah karena menunggu.
        Assert.Equal(Statuses.Failed, result!.Status);
        var row = await GetItemAsync(id);
        Assert.Equal((Statuses.Failed, 1, null), (row.Status, row.RetryCount, row.LockedBy));
        Assert.Null(await repo.ClaimQueueItemAsync("run2"));
        Assert.Equal(conversation, result.Apology!.Value.ConversationId);
        await using var db = NewContext();
        var message = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal((Replies.ModelUnavailable, true, TestModel), (message.Content, message.IsVisible, message.ModelAlias));
    }

    [Fact]
    public async Task Terminal_failure_of_a_job_callback_ticket_answers_instead_of_hanging_the_conversation()
    {
        var repo = NewRepo();
        var conversation = await NewConversationAsync();
        var id = await EnqueueAsync(conversation, QueueTypes.JobCompleted, policy: Policy(maxRetries: 3));
        await repo.ClaimQueueItemAsync("run1");

        var result = await repo.FailQueueItemTerminallyAsync(id, "run1", "model dicabut", Replies.ModelUnavailable);

        Assert.Equal(Statuses.Failed, result!.Status);
        await using var db = NewContext();
        var message = await db.Messages.AsNoTracking().SingleAsync();
        Assert.Equal((Replies.ModelUnavailable, true, TestModel), (message.Content, message.IsVisible, message.ModelAlias));
    }
}
