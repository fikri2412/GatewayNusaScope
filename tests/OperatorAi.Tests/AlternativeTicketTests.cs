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
/// Tiket alternatif (<c>is_primary = 0</c>: regenerate/bandingkan) dan flag barisnya: jawaban alternatif tidak pernah
/// masuk konteks tiket lain, tiket alternatif tetap melihat barisnya sendiri, dan callback job mewarisi flag job-nya.
/// </summary>
public class AlternativeTicketTests : QueueTestBase
{
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

    /// <summary>Klaim tiket berikutnya lalu proses; id diharapkan supaya tiket yang salah urutan tidak lolos diam-diam.</summary>
    private async Task RunTicketAsync(string runId, long expectedItemId, IAiClient ai)
    {
        var item = (await NewRepo().ClaimQueueItemAsync(runId))!;
        Assert.Equal(expectedItemId, item.Id);
        await NewProcessor(runId, ai).HandleAsync(item, CancellationToken.None);
    }

    /// <summary>Tiket regenerate/bandingkan seperti tulisan API: tipe UserMessage, sumber pesan S, tanpa pesan user baru.</summary>
    private async Task<long> NewAlternativeTicketAsync(Guid conversationId, long sourceMessageId, string model = TestModel)
    {
        await using var db = NewContext();
        var item = new QueueItem
        {
            ConversationId = conversationId, Type = QueueTypes.UserMessage,
            PayloadJson = JsonSerializer.Serialize(new { messageId = sourceMessageId, model }),
            ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = sourceMessageId, IsPrimary = false,
        };
        db.QueueItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>Job buatan tiket alternatif (mis. dari <c>analisis_file</c>): menunggu diklaim, flag 0.</summary>
    private async Task<long> NewAlternativeJobAsync(Guid conversationId, long sourceMessageId, int maxRetries = 3)
    {
        await using var db = NewContext();
        var job = new Job
        {
            ConversationId = conversationId, JobType = JobTypes.ExcelRead, InputJson = PacketJson(),
            NotifyOperator = true, SourceMessageId = sourceMessageId, IsPrimary = false,
            ExecutionPolicyJson = Policy(maxRetries: maxRetries).ToJson(),
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<List<Message>> MessagesAsync()
    {
        await using var db = NewContext();
        return await db.Messages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
    }

    [Fact]
    public async Task An_alternative_answer_is_never_part_of_a_later_primary_ticket_input()
    {
        var conversation = await NewConversationAsync();
        var (firstMessage, firstItem) = await NewUserTicketAsync(conversation, "Halo");
        await RunTicketAsync("run1", firstItem, new ScriptedAi(_ => Text("Hai!")));
        var alternativeItem = await NewAlternativeTicketAsync(conversation, firstMessage);
        await RunTicketAsync("run2", alternativeItem, new ScriptedAi(_ => Text("Versi alternatif.")));
        var (_, nextItem) = await NewUserTicketAsync(conversation, "Lanjut");

        var ai = new ScriptedAi(_ => Text("Oke."));
        await RunTicketAsync("run3", nextItem, ai);

        Assert.Equal([(MessageRoles.User, "Halo"), (MessageRoles.Assistant, "Hai!"), (MessageRoles.User, "Lanjut")],
            ai.Requests[0].Messages.Select(m => (m.Role, m.Content)));
    }

    [Fact]
    public async Task A_primary_ticket_still_sees_the_whole_primary_conversation()
    {
        var conversation = await NewConversationAsync();
        var (_, firstItem) = await NewUserTicketAsync(conversation, "Halo");
        await RunTicketAsync("run1", firstItem, new ScriptedAi(_ => Text("Hai!")));
        var (_, nextItem) = await NewUserTicketAsync(conversation, "Lanjut");

        var ai = new ScriptedAi(_ => Text("Oke."));
        await RunTicketAsync("run2", nextItem, ai);

        Assert.Equal([(MessageRoles.User, "Halo"), (MessageRoles.Assistant, "Hai!"), (MessageRoles.User, "Lanjut")],
            ai.Requests[0].Messages.Select(m => (m.Role, m.Content)));
    }

    [Fact]
    public async Task A_regenerate_ticket_only_sees_the_history_up_to_its_source_message()
    {
        var conversation = await NewConversationAsync();
        var (firstMessage, firstItem) = await NewUserTicketAsync(conversation, "Pertanyaan awal");
        await RunTicketAsync("run1", firstItem, new ScriptedAi(_ => Text("Jawaban awal")));
        var (_, secondItem) = await NewUserTicketAsync(conversation, "Pertanyaan lanjutan");
        await RunTicketAsync("run2", secondItem, new ScriptedAi(_ => Text("Jawaban lanjutan")));
        var regenerateItem = await NewAlternativeTicketAsync(conversation, firstMessage);

        var ai = new ScriptedAi(_ => Text("Jawaban alternatif"));
        await RunTicketAsync("run3", regenerateItem, ai);

        // Giliran setelah pesan sumber — termasuk jawaban pertamanya sendiri — tidak ikut.
        Assert.Equal([(MessageRoles.User, "Pertanyaan awal")], ai.Requests[0].Messages.Select(m => (m.Role, m.Content)));
        var rows = await MessagesAsync();
        Assert.Equal([(MessageRoles.User, "Pertanyaan awal"), (MessageRoles.Assistant, "Jawaban awal"),
                (MessageRoles.User, "Pertanyaan lanjutan"), (MessageRoles.Assistant, "Jawaban lanjutan"),
                (MessageRoles.Assistant, "Jawaban alternatif")],
            rows.Select(m => (m.Role, m.Content)));
    }

    [Fact]
    public async Task A_comparison_branch_may_answer_with_another_model_than_its_source_message()
    {
        var conversation = await NewConversationAsync();
        var (firstMessage, firstItem) = await NewUserTicketAsync(conversation, "Halo");
        await RunTicketAsync("run1", firstItem, new ScriptedAi(_ => Text("Hai!")));
        var branchItem = await NewAlternativeTicketAsync(conversation, firstMessage, model: "model-pembanding");

        var ai = new ScriptedAi(_ => Text("Jawaban pembanding."));
        await RunTicketAsync("run2", branchItem, ai);
        Assert.Equal("model-pembanding", Assert.Single(ai.Requests).Model);

        await using var db = NewContext();
        Assert.Equal(Statuses.Done, (await db.QueueItems.SingleAsync(q => q.Id == branchItem)).Status);
        var answer = await db.Messages.SingleAsync(m => m.Content == "Jawaban pembanding.");
        Assert.Equal(("model-pembanding", false), (answer.ModelAlias, answer.IsPrimary));
    }

    [Fact]
    public async Task An_alternative_ticket_keeps_its_own_tool_rows_and_its_jobs_stay_alternative()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);
        var (source, sourceItem) = await NewUserTicketAsync(conversation, "tolong analisis", attachment);
        await RunTicketAsync("run1", sourceItem, new ScriptedAi(_ => Text("Baik.")));
        var alternativeItem = await NewAlternativeTicketAsync(conversation, source);

        var args = JsonSerializer.Serialize(new { attachment_id = attachment, tujuan = "ringkas", buat_pdf = false });
        var ai = new ScriptedAi(_ => ToolCall(ToolDefinitions.AnalisisFile, args), _ => Text("Analisis sedang diproses."));
        await RunTicketAsync("run2", alternativeItem, ai);

        // Putaran kedua tiket alternatif: pesan sumber, tool call sendiri, dan hasil tool-nya (is_primary = 0).
        Assert.Equal([MessageRoles.User, MessageRoles.Assistant, "tool"], ai.Requests[1].Messages.Select(m => m.Role));
        Assert.Equal("Job analisis dibuat dan sedang diproses.", ai.Requests[1].Messages[2].Content);

        await using var db = NewContext();
        var rows = await db.Messages.AsNoTracking().Where(m => m.QueueItemId == alternativeItem).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal([MessageRoles.Assistant, MessageRoles.User, MessageRoles.Assistant], rows.Select(m => m.Role));
        Assert.Equal("Analisis sedang diproses.", rows[^1].Content);
        Assert.All(rows, m => Assert.False(m.IsPrimary));
        var jobs = await db.Jobs.AsNoTracking().ToListAsync();
        Assert.Equal([JobTypes.ExcelRead, JobTypes.Analyze], jobs.Select(j => j.JobType));
        Assert.All(jobs, j => Assert.False(j.IsPrimary));
    }

    [Fact]
    public async Task A_job_callback_inherits_the_flag_of_its_job()
    {
        var conversation = await NewConversationAsync();
        var (source, sourceItem) = await NewUserTicketAsync(conversation, "analisis file saya");
        await RunTicketAsync("run1", sourceItem, new ScriptedAi(_ => Text("Baik.")));
        var repo = NewRepo();

        var completed = await NewAlternativeJobAsync(conversation, source);
        Assert.Equal(completed, (await repo.ClaimJobAsync("jobrun", [JobTypes.ExcelRead]))!.Id);
        Assert.True(await repo.CompleteJobAsync(completed, "jobrun", """{"fileName":"x.xlsx"}"""));

        var callback = (await repo.ClaimQueueItemAsync("run2"))!;
        Assert.Equal((QueueTypes.JobCompleted, false), (callback.Type, callback.IsPrimary));
        await NewProcessor("run2", new ScriptedAi(_ => Text("File sudah saya periksa."))).HandleAsync(callback, CancellationToken.None);

        var failed = await NewAlternativeJobAsync(conversation, source, maxRetries: 1);
        Assert.Equal(failed, (await repo.ClaimJobAsync("jobrun", [JobTypes.ExcelRead]))!.Id);
        Assert.Equal(Statuses.Failed, (await repo.FailJobAsync(failed, "jobrun", "file rusak"))!.Status);

        await using var db = NewContext();
        var rows = await db.Messages.AsNoTracking().Where(m => m.QueueItemId == callback.Id).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal([MessageRoles.User, MessageRoles.Assistant], rows.Select(m => m.Role));   // catatan data job + balasan akhir
        Assert.All(rows, m => Assert.False(m.IsPrimary));
        Assert.Equal("File sudah saya periksa.", rows[^1].Content);
        Assert.False((await db.QueueItems.AsNoTracking().SingleAsync(q => q.Type == QueueTypes.JobFailed)).IsPrimary);
    }
}
