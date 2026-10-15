using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator;
using OperatorAi.Worker.Operator.Tools;

namespace OperatorAi.Tests;

public class StreamingTicketTests : QueueTestBase
{
    private sealed class StreamingAi(Func<AiRequest, Func<string, CancellationToken, Task>, CancellationToken, Task<AiResponse>> produce) : IAiClient
    {
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct) => throw new InvalidOperationException("Expected streaming permission from ticket snapshot");
        public Task<AiResponse> CompleteStreamingAsync(AiRequest request, Func<string, CancellationToken, Task> onTextDelta, CancellationToken ct) => produce(request, onTextDelta, ct);
    }

    private sealed class Capture(HttpStatusCode status = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<StreamEvent> Events = new();
        public readonly TaskCompletionSource FirstContent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/internal/stream")
            {
                var batch = (await request.Content!.ReadFromJsonAsync<StreamEvent>(cancellationToken: ct))!;
                Events.Enqueue(batch);
                if (batch.Kind == "delta") FirstContent.TrySetResult();
                return new(status);
            }
            return new(HttpStatusCode.NoContent);
        }
    }

    private TicketProcessor Processor(string runId, IAiClient ai, Capture capture) => new(
        new WorkerRun(runId), NewRepo(), new NotifyClient(new HttpClient(capture) { BaseAddress = new("http://api.test/") },
            NullLogger<NotifyClient>.Instance), ai, DbOptions, NullLogger<TicketProcessor>.Instance);

    private async Task StreamEnabledAsync(long itemId)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE queue_items SET payload_json = JSON_MODIFY(payload_json, '$.supportsStreaming', CAST(1 AS bit)) WHERE id = {itemId}");
    }

    private static AiResponse Answer(string text) => new(text, "stop", 10, 5, new("assistant", text), true);

    [Fact]
    public async Task Content_is_visible_before_tool_commit_and_retry_does_not_duplicate_committed_jobs()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);
        var (_, itemId) = await NewUserTicketAsync(conversation, "analisis file", attachment);
        await StreamEnabledAsync(itemId);
        var capture = new Capture();
        var finishTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var round = 0;
        var ai = new StreamingAi(async (_, delta, ct) =>
        {
            if (round++ == 0)
            {
                await delta("Saya mulai.", ct);
                await finishTurn.Task.WaitAsync(ct);
                var calls = new JsonArray(new JsonObject
                {
                    ["id"] = "call_1", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = ToolDefinitions.AnalisisFile,
                        ["arguments"] = $$"""{"attachment_id":"{{attachment}}","tujuan":"analisis"}""" }
                });
                return new AiResponse("Saya mulai.", "tool_calls", 10, 5, new("assistant", "Saya mulai.", calls), true);
            }
            await delta("Jawaban yang belum selesai", ct);
            throw new AiStreamException("interrupted", 4, 3, true, false);
        });
        var item = (await NewRepo().ClaimQueueItemAsync("old"))!;
        var handling = Processor("old", ai, capture).HandleAsync(item, CancellationToken.None);
        await capture.FirstContent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var db = NewContext())
        {
            Assert.Empty(await db.Jobs.ToListAsync());
            Assert.Single(await db.Messages.ToListAsync());
        }
        finishTurn.SetResult();
        await Assert.ThrowsAsync<AiStreamException>(() => handling);
        await NewRepo().FailQueueItemAsync(itemId, "old", "interrupted");

        var retry = new Capture();
        var resumed = new StreamingAi(async (request, delta, ct) =>
        {
            Assert.Equal("call_1", request.Messages.Last(m => m.Role == "tool").ToolCallId);
            await delta("Selesai.", ct);
            return Answer("Selesai.");
        });
        await Processor("new", resumed, retry).HandleAsync((await NewRepo().ClaimQueueItemAsync("new"))!, CancellationToken.None);
        await using var check = NewContext();
        Assert.Equal(2, await check.Jobs.CountAsync());
        var assistant = await check.Messages.Where(m => m.Role == MessageRoles.Assistant).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(new[] { "Saya mulai.", "Selesai." }, assistant.Select(m => m.Content));
        Assert.Equal(capture.Events.First(e => e.Round == 0).StreamId, assistant[0].StreamId);
        Assert.Equal(retry.Events.First().StreamId, assistant[1].StreamId);
        Assert.NotEqual(assistant[0].StreamId, assistant[1].StreamId);
        Assert.Equal(Statuses.Done, (await GetItemAsync(itemId)).Status);
        Assert.Equal(37, await check.TokensUsedAsync(conversation, CancellationToken.None));
        Assert.Contains(capture.Events, e => e.Kind == "aborted" && e.Round == 1);
        Assert.All(await check.Jobs.ToListAsync(), j => Assert.True(ModelSnapshot.SupportsStreaming(j.InputJson)));
    }

    [Fact]
    public async Task Failed_draft_delivery_does_not_lose_the_atomic_canonical_reply()
    {
        var conversation = await NewConversationAsync();
        var (_, itemId) = await NewUserTicketAsync(conversation, "halo");
        await StreamEnabledAsync(itemId);
        var capture = new Capture(HttpStatusCode.ServiceUnavailable);
        var ai = new StreamingAi(async (_, delta, ct) => { await delta("Utuh.", ct); return Answer("Utuh."); });
        await Processor("run", ai, capture).HandleAsync((await NewRepo().ClaimQueueItemAsync("run"))!, CancellationToken.None);
        await using var db = NewContext();
        var reply = await db.Messages.SingleAsync(m => m.Role == MessageRoles.Assistant);
        Assert.Equal("Utuh.", reply.Content);
        Assert.NotNull(reply.StreamId);
        Assert.Equal(Statuses.Done, (await GetItemAsync(itemId)).Status);
    }

    [Fact]
    public async Task Canceled_partial_stream_marks_usage_unknown_and_releases_without_spending_retry()
    {
        var conversation = await NewConversationAsync();
        var (_, itemId) = await NewUserTicketAsync(conversation, "jelaskan");
        await StreamEnabledAsync(itemId);
        using var stop = new CancellationTokenSource();
        var capture = new Capture();
        var ai = new StreamingAi(async (_, delta, ct) =>
        {
            await delta("Sebagian", ct);
            await capture.FirstContent.Task.WaitAsync(ct);
            stop.Cancel();
            throw new AiStreamException("canceled", 0, 0, false, true, new OperationCanceledException(ct));
        });
        var claimed = (await NewRepo().ClaimQueueItemAsync("run"))!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Processor("run", ai, capture)
            .HandleAsync(claimed, stop.Token));
        await NewRepo().ReleaseRunAsync("run");
        await using var db = NewContext();
        Assert.Single(await db.Messages.ToListAsync());
        var conv = await db.Conversations.SingleAsync(c => c.Id == conversation);
        Assert.True(conv.UsageIncomplete);
        Assert.Equal(0, conv.TokensUsed);
        var item = await GetItemAsync(itemId);
        Assert.Equal(Statuses.Pending, item.Status);
        Assert.Equal(0, item.RetryCount);
    }
}
