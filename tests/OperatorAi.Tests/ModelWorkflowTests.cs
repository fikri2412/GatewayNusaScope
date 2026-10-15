using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator;
using OperatorAi.Worker.Operator.Tools;
using OperatorAi.Worker.Specialist;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

/// <summary>
/// §3/6A2: model workflow dibaca sekali dari snapshot tiket/job, dipakai apa adanya oleh Operator, Analyze, dan PDF
/// (termasuk retry, callback, dan recovery), dan model yang dicabut gateway berhenti permanen setelah satu percobaan
/// dengan balasan yang jelas. Tidak ada fallback ke model percakapan atau default mana pun.
/// </summary>
public class ModelWorkflowTests : QueueTestBase
{
    private const string ModelA = "model-A", ModelB = "model-B";

    private static AiResponse Text(string text) => new(text, "stop", 10, 5, new AiMessage("assistant", text),
        UsageAvailable: true);

    private static AiResponse ToolCall(string name, string argsJson, string id = "call_1") =>
        new("", "tool_calls", 10, 5, new AiMessage("assistant", "",
            (JsonArray)JsonNode.Parse(
                $$$"""[{"index":0,"id":"{{{id}}}","type":"function","function":{"name":"{{{name}}}","arguments":{{{JsonSerializer.Serialize(argsJson)}}}}}]""")!),
        UsageAvailable: true);

    private static NotifyClient NewNotify(NotifyStub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://api.test/") }, NullLogger<NotifyClient>.Instance);

    private TicketProcessor NewProcessor(string runId, NotifyStub stub, ScriptedAi ai) =>
        new(new WorkerRun(runId), NewRepo(), NewNotify(stub), ai, DbOptions, NullLogger<TicketProcessor>.Instance);

    private OperatorWorker NewOperatorWorker(string runId, NotifyStub stub, ScriptedAi ai) =>
        new(new WorkerRun(runId), NewRepo(), NewNotify(stub), NewProcessor(runId, stub, ai), new TestLifetime(),
            NullLogger<OperatorWorker>.Instance);

    private SpecialistWorker NewSpecialistWorker(NotifyStub stub, params IJobHandler[] handlers) =>
        new(new WorkerRun("spec-run"), NewRepo(), NewNotify(stub), handlers, DbOptions, new TestLifetime(),
            NullLogger<SpecialistWorker>.Instance);

    private static async Task RunWorkerAsync(BackgroundService worker)
    {
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;
    }

    private async Task<QueueItem> ClaimAsync(string runId) => (await NewRepo().ClaimQueueItemAsync(runId))!;

    private async Task<List<Message>> MessagesAsync()
    {
        await using var db = NewContext();
        return await db.Messages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
    }

    /// <summary>Metadata tool apa pun selalu membawa model workflow, seperti yang ditulis <c>AddJobAsync</c>.</summary>
    private static string Input(string model, object fields)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(fields, JsonSerializerOptions.Web))!;
        node["model"] = model;
        return node.ToJsonString();
    }

    /// <summary>Kiriman berikutnya oleh user: pilihan model baru tersimpan di percakapan, riwayat lama tidak berubah.</summary>
    private async Task SwitchToModelBAsync(Guid conversationId)
    {
        await using var db = NewContext();
        await db.Conversations.Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ModelAlias, ModelB));
        db.Messages.Add(new Message { ConversationId = conversationId, Role = MessageRoles.User, Content = "pesan berikutnya", ModelAlias = ModelB });
        await db.SaveChangesAsync();
    }

    /// <summary>Rantai job ExcelRead (Done) → Analyze (Pending) → Pdf (Pending) milik satu pesan user.</summary>
    private async Task<(long SourceMessageId, long AnalyzeJobId, long PdfJobId)> NewChainAsync(Guid conversationId, string model)
    {
        await using var db = NewContext();
        var message = new Message
        {
            ConversationId = conversationId, Role = MessageRoles.User, Content = "analisis penjualan", ModelAlias = model,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        var policy = Policy().ToJson();
        var excel = new Job
        {
            ConversationId = conversationId, JobType = JobTypes.ExcelRead, InputJson = Input(model, new { attachmentId = Guid.NewGuid() }),
            Status = Statuses.Done, OutputJson = """{"fileName":"penjualan.xlsx"}""", ExecutionPolicyJson = policy,
            SourceMessageId = message.Id,
        };
        var analyze = new Job
        {
            ConversationId = conversationId, JobType = JobTypes.Analyze, InputJson = Input(model, new { tujuan = "ringkas" }),
            ExecutionPolicyJson = policy, SourceMessageId = message.Id,
        };
        db.Jobs.AddRange(excel, analyze);
        await db.SaveChangesAsync();
        analyze.DependsOnJobId = excel.Id;
        var pdf = new Job
        {
            ConversationId = conversationId, JobType = JobTypes.Pdf,
            InputJson = Input(model, new { judul = "Laporan", langsung = true, analyzeJobId = analyze.Id }),
            DependsOnJobId = analyze.Id, NotifyOperator = true, ExecutionPolicyJson = policy, SourceMessageId = message.Id,
        };
        db.Jobs.Add(pdf);
        await db.SaveChangesAsync();
        return (message.Id, analyze.Id, pdf.Id);
    }

    [Fact]
    public async Task Operator_and_analyze_and_pdf_callbacks_keep_the_model_of_their_own_send()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);
        var (_, itemId) = await NewUserTicketAsync(conversation, "tolong analisis", ModelA, attachment);
        var args = JsonSerializer.Serialize(new { attachment_id = attachment, tujuan = "ringkas", buat_pdf = false });
        var operatorStub = new NotifyStub();
        var operatorAi = new ScriptedAi(_ => ToolCall(ToolDefinitions.AnalisisFile, args), _ => Text("Analisis sedang diproses."));

        await NewProcessor("run1", operatorStub, operatorAi).HandleAsync(await ClaimAsync("run1"), CancellationToken.None);

        Assert.Equal([ModelA, ModelA], operatorAi.Requests.Select(r => r.Model));
        var repo = NewRepo();
        var excel = await repo.ClaimJobAsync("spec1", [JobTypes.ExcelRead]);
        Assert.True(await repo.CompleteJobAsync(excel!.Id, "spec1", """{"fileName":"penjualan.xlsx"}"""));
        var analyze = await repo.ClaimJobAsync("spec2", [JobTypes.Analyze]);
        Assert.True(await repo.CompleteJobAsync(analyze!.Id, "spec2", """{"judul":"Analisis","ringkasan":"r"}"""));

        // User berikutnya memilih model lain; job dan tiket yang sudah ada tetap milik kiriman pertama.
        await SwitchToModelBAsync(conversation);

        var callbackStub = new NotifyStub();
        var callbackAi = new ScriptedAi(_ => Text("File sudah saya periksa."));
        await NewProcessor("run2", callbackStub, callbackAi).HandleAsync(await ClaimAsync("run2"), CancellationToken.None);

        Assert.Equal(ModelA, Assert.Single(callbackAi.Requests).Model);
        await using var db = NewContext();
        Assert.All(await db.Jobs.AsNoTracking().ToListAsync(), job => Assert.Equal(ModelA, ModelSnapshot.Read(job.InputJson)));
        var reply = (await MessagesAsync()).Last();
        Assert.Equal((ModelA, true, "File sudah saya periksa."), (reply.ModelAlias, reply.IsVisible, reply.Content));
        Assert.Equal(Statuses.Done, (await GetItemAsync(itemId)).Status);
    }

    [Fact]
    public async Task Pdf_callback_reply_is_labelled_with_the_originating_model_without_an_ai_call()
    {
        var conversation = await NewConversationAsync();
        var pdf = await NewAttachmentAsync(conversation, "laporan.pdf", AttachmentKinds.Output);
        long itemId;
        await using (var db = NewContext())
        {
            var message = new Message
            {
                ConversationId = conversation, Role = MessageRoles.User, Content = "buatkan PDF", ModelAlias = ModelA,
            };
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            var job = new Job
            {
                ConversationId = conversation, JobType = JobTypes.Pdf, InputJson = Input(ModelA, new { judul = "Laporan", langsung = true }),
                Status = Statuses.Done, OutputJson = JsonSerializer.Serialize(new { attachmentId = pdf, fileName = "laporan.pdf" }),
                NotifyOperator = true, ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = message.Id,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            var item = new QueueItem
            {
                ConversationId = conversation, Type = QueueTypes.JobCompleted,
                PayloadJson = JsonSerializer.Serialize(new { jobId = job.Id, model = ModelA, jobType = JobTypes.Pdf }),
                ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = message.Id,
            };
            db.QueueItems.Add(item);
            await db.SaveChangesAsync();
            itemId = item.Id;
        }

        await SwitchToModelBAsync(conversation);
        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => Text("tidak boleh dipanggil"));

        await NewProcessor("run1", stub, ai).HandleAsync(await ClaimAsync("run1"), CancellationToken.None);

        Assert.Empty(ai.Requests);
        var reply = (await MessagesAsync()).Last();
        Assert.Equal((ModelA, true, $"[\"{pdf}\"]"), (reply.ModelAlias, reply.IsVisible, reply.AttachmentIdsJson));
        Assert.Equal(Statuses.Done, (await GetItemAsync(itemId)).Status);
    }

    [Fact]
    public async Task Ticket_without_a_model_is_invalid_and_never_uses_a_default()
    {
        var conversation = await NewConversationAsync();
        var item = new QueueItem
        {
            Id = 1, ConversationId = conversation, Type = QueueTypes.UserMessage, PayloadJson = """{"messageId":1}""",
            ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = 1, Status = Statuses.Processing, LockedBy = "run1",
        };
        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => Text("tidak boleh dipanggil"));

        await Assert.ThrowsAsync<FormatException>(() => NewProcessor("run1", stub, ai).HandleAsync(item, CancellationToken.None));

        Assert.Empty(ai.Requests);
        await using var db = NewContext();
        Assert.Empty(db.Messages);
    }

    [Fact]
    public async Task Ticket_whose_source_message_uses_another_model_is_rejected_before_any_ai_call()
    {
        var conversation = await NewConversationAsync();
        var (messageId, itemId) = await NewUserTicketAsync(conversation, "halo", ModelA);
        await using (var db = NewContext())
        {
            // Tiket memakai model A, pesan sumbernya A juga; baris yang tak cocok harus ditolak, bukan diikuti diam-diam.
            await db.Messages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelAlias, ModelB));
        }

        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => Text("tidak boleh dipanggil"));
        var item = await ClaimAsync("run1");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProcessor("run1", stub, ai).HandleAsync(item, CancellationToken.None));

        Assert.Empty(ai.Requests);
        Assert.Equal(Statuses.Processing, (await GetItemAsync(itemId)).Status);   // worker yang memutuskan gagal/retry
    }

    [Fact]
    public async Task Revoked_model_ends_the_operator_ticket_after_one_attempt_with_one_visible_reply()
    {
        var conversation = await NewConversationAsync();
        var (_, itemId) = await NewUserTicketAsync(conversation, "halo", ModelA);
        await SetQueuePolicyAsync(itemId, Policy(maxRetries: 3));
        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => throw new AiModelUnavailableException(ModelA));

        await RunWorkerAsync(NewOperatorWorker("run1", stub, ai));

        var row = await GetItemAsync(itemId);
        Assert.Equal((Statuses.Failed, 1), (row.Status, row.RetryCount));       // satu percobaan, sisa retry tidak dipakai
        Assert.Null(await NewRepo().ClaimQueueItemAsync("run2"));
        Assert.Equal(ModelA, Assert.Single(ai.Requests).Model);
        var reply = Assert.Single(await MessagesAsync(), m => m.Role == MessageRoles.Assistant);
        Assert.Equal((Replies.ModelUnavailable, true, ModelA), (reply.Content, reply.IsVisible, reply.ModelAlias));
        Assert.Equal([NotifyTypes.MessageCreated], stub.Sent.Select(s => s.Type));
    }

    [Fact]
    public async Task A_provider_that_never_starts_answering_ends_the_ticket_at_once_with_a_clear_reply_and_no_retry()
    {
        var conversation = await NewConversationAsync();
        var (_, itemId) = await NewUserTicketAsync(conversation, "halo", ModelA);
        await SetQueuePolicyAsync(itemId, Policy(maxRetries: 3));
        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => throw new AiFirstByteTimeoutException(30));

        await RunWorkerAsync(NewOperatorWorker("run1", stub, ai));

        // Satu percobaan: mengulang langsung hanya melipatgandakan penantian user (antrean penyedia mulai dari nol).
        var row = await GetItemAsync(itemId);
        Assert.Equal((Statuses.Failed, 1), (row.Status, row.RetryCount));
        Assert.Null(await NewRepo().ClaimQueueItemAsync("run2"));
        var reply = Assert.Single(await MessagesAsync(), m => m.Role == MessageRoles.Assistant);
        Assert.Equal((Replies.ProviderSlow, true, ModelA), (reply.Content, reply.IsVisible, reply.ModelAlias));
        Assert.Equal([NotifyTypes.MessageCreated], stub.Sent.Select(s => s.Type));
    }

    [Fact]
    public async Task A_stale_ticket_that_blocks_the_conversation_is_recovered_by_the_worker_which_then_claims_it()
    {
        var conversation = await NewConversationAsync();
        var (_, staleId) = await NewUserTicketAsync(conversation, "satu", ModelA);
        await ClaimAsync("crashed");                     // proses sebelumnya mati sambil memegang tiket ini
        await AgeLockAsync(staleId, minutes: 11);
        var (_, nextId) = await NewUserTicketAsync(conversation, "dua", ModelA);
        var ai = new ScriptedAi(_ => Text("Halo"));

        // Recovery membuka percakapan yang tertahan; satu proses kemudian menyelesaikan tiket tertua.
        await RunWorkerAsync(NewOperatorWorker("run1", new NotifyStub(), ai));

        var recovered = await GetItemAsync(staleId);
        Assert.Equal((Statuses.Done, 1), (recovered.Status, recovered.RetryCount));
        Assert.Equal(Statuses.Pending, (await GetItemAsync(nextId)).Status);       // satu proses = satu tiket
        Assert.Equal("satu", Assert.Single(ai.Requests).Messages[^1].Content);
    }

    [Fact]
    public async Task Transient_ai_failure_still_goes_back_to_pending_without_answering_the_user()
    {
        var conversation = await NewConversationAsync();
        var (_, itemId) = await NewUserTicketAsync(conversation, "halo", ModelA);
        await SetQueuePolicyAsync(itemId, Policy(maxRetries: 2));
        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => throw new AiException("gangguan jaringan"));

        await RunWorkerAsync(NewOperatorWorker("run1", stub, ai));

        var row = await GetItemAsync(itemId);
        Assert.Equal((Statuses.Pending, 1, null), (row.Status, row.RetryCount, row.LockedBy));   // gangguan sementara dicoba lagi
        Assert.Equal(ModelA, Assert.Single(ai.Requests).Model);
        Assert.Single(await MessagesAsync());                                   // belum ada balasan yang mengarang hasil
    }

    [Fact]
    public async Task Revoked_model_terminalizes_the_specialist_job_and_its_dependents_after_one_attempt()
    {
        var conversation = await NewConversationAsync();
        var (_, analyzeId, pdfId) = await NewChainAsync(conversation, ModelA);
        // User sudah memilih model lain untuk kiriman berikutnya; job Analyze tetap milik model asalnya.
        await SwitchToModelBAsync(conversation);
        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => throw new AiModelUnavailableException(ModelA));

        await RunWorkerAsync(NewSpecialistWorker(stub, new AnalyzeHandler(ai, DbOptions, NullLogger<AnalyzeHandler>.Instance)));

        var analyze = await GetJobAsync(analyzeId);
        Assert.Equal((Statuses.Failed, 1), (analyze.Status, analyze.RetryCount));
        Assert.Equal(ModelA, Assert.Single(ai.Requests).Model);
        Assert.Equal((Statuses.Failed, "Dependency failed"), ((await GetJobAsync(pdfId)).Status, (await GetJobAsync(pdfId)).Error));
        Assert.Null(await NewRepo().ClaimJobAsync("spec2", JobTypes.All));

        await using var db = NewContext();
        var queued = Assert.Single(await db.QueueItems.AsNoTracking().Where(q => q.ConversationId == conversation).ToListAsync());
        Assert.Equal(QueueTypes.JobFailed, queued.Type);
        var payload = JsonDocument.Parse(queued.PayloadJson).RootElement;
        Assert.Equal((analyzeId, ModelA, FailureCategories.ModelUnavailable),
            (payload.GetProperty("jobId").GetInt64(), payload.GetProperty("model").GetString(),
             payload.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task Callback_of_a_revoked_model_answers_without_another_ai_call()
    {
        var conversation = await NewConversationAsync();
        await NewChainAsync(conversation, ModelA);
        var specialistStub = new NotifyStub();
        await RunWorkerAsync(NewSpecialistWorker(specialistStub,
            new AnalyzeHandler(new ScriptedAi(_ => throw new AiModelUnavailableException(ModelA)), DbOptions,
                NullLogger<AnalyzeHandler>.Instance)));

        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => Text("tidak boleh dipanggil"));
        var item = await ClaimAsync("run1");
        await NewProcessor("run1", stub, ai).HandleAsync(item, CancellationToken.None);
        Assert.Equal(QueueTypes.JobFailed, item.Type);
        Assert.Empty(ai.Requests);
        var reply = (await MessagesAsync()).Last();
        Assert.Equal((Replies.ModelUnavailable, true, ModelA), (reply.Content, reply.IsVisible, reply.ModelAlias));
        Assert.Equal(Statuses.Done, (await GetItemAsync(item.Id)).Status);
        Assert.Equal([NotifyTypes.MessageCreated], stub.Sent.Select(s => s.Type));
    }

    [Fact]
    public async Task Specialist_job_whose_source_message_uses_another_model_never_calls_ai()
    {
        var conversation = await NewConversationAsync();
        var (sourceMessageId, analyzeId, _) = await NewChainAsync(conversation, ModelA);
        await using (var db = NewContext())
        {
            // Job memakai model A, pesan sumbernya diubah jadi B: baris yang tak cocok ditolak, bukan diikuti diam-diam.
            await db.Messages.Where(m => m.Id == sourceMessageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelAlias, ModelB));
        }

        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => Text("tidak boleh dipanggil"));
        await RunWorkerAsync(NewSpecialistWorker(stub, new AnalyzeHandler(ai, DbOptions, NullLogger<AnalyzeHandler>.Instance)));

        Assert.Empty(ai.Requests);
        var job = await GetJobAsync(analyzeId);
        Assert.Equal((Statuses.Pending, 1), (job.Status, job.RetryCount));      // baris tak cocok dianggap gagal, bukan diikuti
    }

    [Fact]
    public async Task Alternative_specialist_job_may_run_on_another_model_than_its_source_message()
    {
        var conversation = await NewConversationAsync();
        var (sourceMessageId, analyzeId, _) = await NewChainAsync(conversation, ModelA);
        await using (var db = NewContext())
        {
            // Job alternatif (pilihan branch) memakai model A, pesan sumbernya model B: job tetap jalan dengan modelnya sendiri.
            await db.Jobs.Where(j => j.Id == analyzeId).ExecuteUpdateAsync(s => s.SetProperty(j => j.IsPrimary, false));
            await db.Messages.Where(m => m.Id == sourceMessageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelAlias, ModelB));
        }

        var stub = new NotifyStub();
        var ai = new ScriptedAi(_ => Text("""{"judul":"Analisis","ringkasan":"Ringkasan","temuan_masalah":[],"rekomendasi_digitalisasi":[],"rancangan_tabel":[],"catatan":""}"""));
        await RunWorkerAsync(NewSpecialistWorker(stub, new AnalyzeHandler(ai, DbOptions, NullLogger<AnalyzeHandler>.Instance)));

        Assert.Equal(ModelA, Assert.Single(ai.Requests).Model);
        Assert.Equal(Statuses.Done, (await GetJobAsync(analyzeId)).Status);
    }
}
