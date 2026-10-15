using System.Text.Json;
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

/// <summary>
/// Statistik tiket pada baris balasan akhir: token semua panggilan model, waktu tunggu antrean, token pertama
/// (hanya jalur streaming), dan durasi total dari baris tiket.
/// </summary>
public class ReplyTimingTests : QueueTestBase
{
    private sealed class StreamingAi(params Func<AiRequest, Func<string, CancellationToken, Task>, CancellationToken, Task<AiResponse>>[] script) : IAiClient
    {
        private int _next;

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Tiket ini meminta streaming.");

        public Task<AiResponse> CompleteStreamingAsync(AiRequest request, Func<string, CancellationToken, Task> onTextDelta,
            CancellationToken ct) => script[Math.Min(_next++, script.Length - 1)](request, onTextDelta, ct);
    }

    private static AiResponse Text(string text) =>
        new(text, "stop", 10, 5, new AiMessage("assistant", text), UsageAvailable: true);

    private static AiResponse ToolCall(string name, string argsJson) => new("", "tool_calls", 10, 5,
        new AiMessage("assistant", "", new JsonArray(new JsonObject
        {
            ["index"] = 0, ["id"] = "call_1", ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argsJson },
        }), ReasoningContent: "mikir"), UsageAvailable: true);

    private TicketProcessor NewProcessor(string runId, IAiClient ai) => new(
        new WorkerRun(runId), NewRepo(),
        new NotifyClient(new HttpClient(new NotifyStub()) { BaseAddress = new Uri("http://api.test/") }, NullLogger<NotifyClient>.Instance),
        ai, DbOptions, NullLogger<TicketProcessor>.Instance);

    private async Task HandleNextTicketAsync(string runId, IAiClient ai) =>
        await NewProcessor(runId, ai).HandleAsync((await NewRepo().ClaimQueueItemAsync(runId))!, CancellationToken.None);

    /// <summary>Izin streaming ditulis di payload tiket, seperti snapshot yang dibuat API.</summary>
    private async Task StreamEnabledAsync(long itemId)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE queue_items SET payload_json = JSON_MODIFY(payload_json, '$.supportsStreaming', CAST(1 AS bit)) WHERE id = {itemId}");
    }

    private async Task<List<Message>> MessagesAsync()
    {
        await using var db = NewContext();
        return await db.Messages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
    }

    private async Task<long> TokensUsedAsync(Guid conversation)
    {
        await using var db = NewContext();
        return await db.TokensUsedAsync(conversation, CancellationToken.None);
    }

    [Fact]
    public async Task A_streamed_answer_writes_the_ticket_tokens_and_timing_on_the_final_reply()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);
        var (_, itemId) = await NewUserTicketAsync(conversation, "tolong analisis", attachment);
        await StreamEnabledAsync(itemId);
        var args = JsonSerializer.Serialize(new { attachment_id = attachment, tujuan = "ringkas", buat_pdf = false });
        var ai = new StreamingAi(
            (_, _, _) => Task.FromResult(ToolCall(ToolDefinitions.AnalisisFile, args)),
            async (_, onDelta, ct) =>
            {
                await onDelta("Hai", ct);
                await onDelta("!", ct);
                return Text("Hai!");
            });

        await HandleNextTicketAsync("run1", ai);

        var rows = await MessagesAsync();
        var reply = rows[^1];
        Assert.Equal((MessageRoles.Assistant, "Hai!"), (reply.Role, reply.Content));
        Assert.True(reply.IsPrimary);
        Assert.Equal(30, reply.TokensUsed);                       // dua panggilan model: 2 x (10 prompt + 5 completion)
        Assert.NotNull(reply.QueueWaitMs);
        Assert.NotNull(reply.FirstTokenMs);                       // delta konten pertama pada panggilan kedua
        Assert.True(reply.TotalMs!.Value >= reply.QueueWaitMs!.Value);

        // Hanya balasan akhir yang membawa statistik; baris tool call tetap kosong.
        var toolRow = rows.Single(m => m.Role == MessageRoles.Assistant && m.Content is null);
        Assert.Null(toolRow.TokensUsed);
        Assert.Null(toolRow.QueueWaitMs);
        Assert.Null(toolRow.FirstTokenMs);
        Assert.Null(toolRow.TotalMs);

        Assert.Equal(30, await TokensUsedAsync(conversation));    // akuntansi token percakapan tidak berubah
    }

    [Fact]
    public async Task A_non_streaming_answer_has_no_first_token_time()
    {
        var conversation = await NewConversationAsync();
        var (_, itemId) = await NewUserTicketAsync(conversation, "halo");

        await HandleNextTicketAsync("run1", new ScriptedAi(_ => Text("Hai!")));

        var reply = (await MessagesAsync()).Single(m => m.QueueItemId == itemId && m.IsVisible);
        Assert.Equal((MessageRoles.Assistant, "Hai!"), (reply.Role, reply.Content));
        Assert.True(reply.IsPrimary);
        Assert.Equal(15, reply.TokensUsed);
        Assert.Null(reply.FirstTokenMs);
        Assert.NotNull(reply.QueueWaitMs);
        Assert.NotNull(reply.TotalMs);
        Assert.Equal(15, await TokensUsedAsync(conversation));
    }
}
