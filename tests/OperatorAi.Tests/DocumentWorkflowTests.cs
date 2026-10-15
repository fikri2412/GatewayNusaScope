using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Shared.Ai;
using OperatorAi.Api;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator;
using OperatorAi.Worker.Operator.Tools;

namespace OperatorAi.Tests;

public class DocumentWorkflowTests : QueueTestBase
{
    private async Task<Guid> ImageAsync(Guid conversation)
    {
        var id = await NewAttachmentAsync(conversation, "chart.png");
        await using var db = NewContext();
        await db.Attachments.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContentType, "image/png"));
        return id;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Unconfirmed_vision_rejects_an_image_without_changing_conversation_or_enqueuing(bool? vision)
    {
        var conversation = await NewConversationAsync();
        var image = await ImageAsync(conversation);
        await using var db = NewContext();
        Task<IReadOnlyList<ModelInfoDto>> Models(CancellationToken _) => Task.FromResult<IReadOnlyList<ModelInfoDto>>(
            [new(TestModel, true, true, true, null, null, vision)]);
        var result = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversation,
            new("Jelaskan grafik ini", [image], TestModel), Models, Policy(), CancellationToken.None);
        Assert.Equal(400, result.Status);
        Assert.Empty(await db.Messages.ToListAsync());
        Assert.Empty(await db.QueueItems.ToListAsync());
        Assert.Null((await db.Conversations.SingleAsync()).ModelAlias);
    }

    [Fact]
    public async Task Six_attachments_are_accepted_but_an_oversized_array_is_rejected_before_catalog_failure()
    {
        var conversation = await NewConversationAsync();
        var images = new List<Guid>();
        for (var i = 0; i < 6; i++) images.Add(await ImageAsync(conversation));
        await using var db = NewContext();
        Task<IReadOnlyList<ModelInfoDto>> Models(CancellationToken _) => Task.FromResult<IReadOnlyList<ModelInfoDto>>(
            [new(TestModel, true, false, true, null, null, true)]);
        var accepted = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversation,
            new("Analisis gambar", images.ToArray(), TestModel), Models, Policy(), CancellationToken.None);
        Assert.Equal(202, accepted.Status);
        var rejected = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversation,
            new("Analisis ulang", Enumerable.Repeat(images[0], 7).ToArray(), TestModel),
            _ => throw new GatewayModelsException(), Policy(), CancellationToken.None);
        Assert.Equal(400, rejected.Status);
        Assert.Single(await db.Messages.ToListAsync());
        Assert.Single(await db.QueueItems.ToListAsync());
    }

    /// <summary>
    /// Batas kirim dibaca dari snapshot kebijakan saat ini, bukan saat lampiran diunggah: berkas lama tidak
    /// dihapus, tapi kiriman baru yang melampaui batas jumlah/ukuran/format ditolak, dan tiket lama tetap
    /// membawa snapshot saat ia diantrekan.
    /// </summary>
    [Fact]
    public async Task Send_uses_the_current_upload_policy_while_old_tickets_keep_their_snapshot()
    {
        var conversation = await NewConversationAsync();
        var oversized = await NewAttachmentAsync(conversation, "catatan.pdf");
        var image = await ImageAsync(conversation);
        var small = await NewAttachmentAsync(conversation, "kecil.pdf");
        await using (var update = NewContext())
            await update.Attachments.Where(a => a.Id == oversized).ExecuteUpdateAsync(s => s.SetProperty(a => a.SizeBytes, 5000L));

        var shrink = Policy(upload: new UploadRuntimeLimits { MaxBytes = 1024, AllowedExtensions = [".pdf"], MaxAttachmentsPerMessage = 2 });
        IReadOnlyList<ModelInfoDto> catalog = [new(TestModel, true, true, true, null, null, true)];
        Task<IReadOnlyList<ModelInfoDto>> Models(CancellationToken _) => Task.FromResult(catalog);

        await using var db = NewContext();
        var conv = await db.Conversations.SingleAsync(c => c.Id == conversation);
        await ConversationEndpoints.EnqueueUserMessageAsync(db, conv, "sebelum", [], TestModel, Policy(), CancellationToken.None);

        // Panjang mentah sebelum Distinct: tiga id (dua unik) sudah melewati batas 2, jadi ditolak sebelum katalog diminta.
        var tooMany = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversation,
            new("tiga", [small, small, image], TestModel), _ => throw new GatewayModelsException(), shrink, CancellationToken.None);
        Assert.Equal(400, tooMany.Status);
        Assert.Contains("Maksimal 2 lampiran", tooMany.Error);

        var tooBig = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversation,
            new("besar", [oversized], TestModel), _ => throw new GatewayModelsException(), shrink, CancellationToken.None);
        Assert.Equal(400, tooBig.Status);
        Assert.Contains("melebihi ukuran maksimal 1 KB", tooBig.Error);

        var wrongFormat = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversation,
            new("gambar", [image], TestModel), _ => throw new GatewayModelsException(), shrink, CancellationToken.None);
        Assert.Equal(400, wrongFormat.Status);
        Assert.Contains("tidak lagi diizinkan", wrongFormat.Error);

        var accepted = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversation,
            new("kirim", [small], TestModel), Models, shrink, CancellationToken.None);
        Assert.Equal(202, accepted.Status);

        Assert.Equal(3, await db.Attachments.CountAsync()); // kebijakan menyempit tidak menghapus lampiran lama
        var items = await db.QueueItems.AsNoTracking().OrderBy(q => q.Id).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal(RuntimeSafetyLimits.UploadBytes, ExecutionPolicy.FromJson(items[0].ExecutionPolicyJson).Upload.MaxBytes);
        Assert.Equal(1024, ExecutionPolicy.FromJson(items[1].ExecutionPolicyJson).Upload.MaxBytes);
    }

    [Fact]
    public async Task Document_tool_cannot_create_work_for_a_foreign_attachment()
    {
        var mine = await NewConversationAsync();
        var foreign = await ImageAsync(await NewConversationAsync());
        await using var db = NewContext();
        var result = await ToolDispatcher.ExecuteAsync(new(db, mine, Policy(), 1, TestModel, true, true),
            ToolDefinitions.AnalisisDokumen, JsonSerializer.Serialize(new { attachment_id = foreign, tujuan = "analisis" }),
            CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Image_job_keeps_vision_permission_after_catalog_changes_and_callback_does_not_repeat_analysis()
    {
        var conversation = await NewConversationAsync();
        var image = await ImageAsync(conversation);
        await using var db = NewContext();
        var conv = await db.Conversations.SingleAsync();
        var (source, _) = await ConversationEndpoints.EnqueueUserMessageAsync(db, conv, "Baca grafik", [image], TestModel,
            Policy(), CancellationToken.None, supportsVision: true);
        var tool = await ToolDispatcher.ExecuteAsync(new(db, conversation, Policy(), source.Id, TestModel, false, true),
            ToolDefinitions.AnalisisDokumen, JsonSerializer.Serialize(new { attachment_id = image, tujuan = "Baca grafik", langsung = true }),
            CancellationToken.None);
        Assert.False(tool.IsError);
        var read = await NewRepo().ClaimJobAsync("reader", JobTypes.All);
        Assert.Equal(JobTypes.DocumentRead, read!.JobType);
        Assert.True(ModelSnapshot.SupportsVision(read.InputJson));
        Assert.True(await NewRepo().CompleteJobAsync(read.Id, "reader", "{}"));
        var analyze = await NewRepo().ClaimJobAsync("analyzer", JobTypes.All);
        Assert.Equal(JobTypes.DocumentAnalyze, analyze!.JobType);
        Assert.True(ModelSnapshot.SupportsVision(analyze.InputJson));
        var output = JsonSerializer.Serialize(new
        {
            judul = "Grafik penjualan", ringkasan = "Barat 80, Timur 40.", penjelasan = "Barat dua kali Timur.",
            poin = new[] { "Selisih 40 unit." }, sumberHalaman = new[] { 1 }, catatan = "Sumber: gambar.", finishReason = "stop",
        });
        Assert.True(await NewRepo().CompleteJobAsync(analyze.Id, "analyzer", output));
        var callback = await NewRepo().ClaimQueueItemAsync("operator");
        // Tiket user masih Pending; selesaikan tanpa AI agar callback dapat diklaim.
        if (callback!.Type == QueueTypes.UserMessage)
        {
            await NewRepo().CompleteQueueItemAsync(callback.Id, "operator");
            callback = await NewRepo().ClaimQueueItemAsync("operator");
        }
        Assert.Equal(QueueTypes.JobCompleted, callback!.Type);
        Assert.True(ModelSnapshot.SupportsVision(callback.PayloadJson));
        var ai = new ScriptedAi(_ => throw new InvalidOperationException("Callback tidak boleh menganalisis ulang."));
        var notify = new NotifyClient(new HttpClient(new NotifyStub()) { BaseAddress = new("http://api.test/") }, NullLogger<NotifyClient>.Instance);
        await new TicketProcessor(new("operator"), NewRepo(), notify, ai, DbOptions, NullLogger<TicketProcessor>.Instance)
            .HandleAsync(callback, CancellationToken.None);
        await using var check = NewContext();
        var reply = await check.Messages.SingleAsync(m => m.Role == MessageRoles.Assistant);
        Assert.Contains("80", reply.Content);
        Assert.Contains("40", reply.Content);
        Assert.Contains("Barat dua kali Timur", reply.Content);
        Assert.Empty(ai.Requests);
        Assert.Equal(Statuses.Done, (await GetItemAsync(callback.Id)).Status);
    }

    [Fact]
    public async Task Invalid_document_terminal_failure_preserves_reason_and_does_not_become_model_revocation()
    {
        var conversation = await NewConversationAsync();
        var (_, sourceTicket) = await NewUserTicketAsync(conversation, "Baca dokumen ini");
        var sourceItem = await GetItemAsync(sourceTicket);
        var readId = await NewJobAsync(conversation, jobType: JobTypes.DocumentRead);
        await using (var db = NewContext())
            await db.Jobs.Where(j => j.Id == readId).ExecuteUpdateAsync(s => s.SetProperty(j => j.SourceMessageId, sourceItem.SourceMessageId));
        await NewRepo().ClaimJobAsync("reader", JobTypes.All);
        await NewRepo().FailJobTerminallyAsync(readId, "reader", "PDF terkunci password.", category: FailureCategories.DocumentInvalid);
        var userTicket = (await NewRepo().ClaimQueueItemAsync("operator"))!;
        await NewRepo().CompleteQueueItemAsync(userTicket.Id, "operator");
        var callback = (await NewRepo().ClaimQueueItemAsync("operator"))!;
        var ai = new ScriptedAi(_ => throw new InvalidOperationException("Error input tidak perlu inferensi."));
        var notify = new NotifyClient(new HttpClient(new NotifyStub()) { BaseAddress = new("http://api.test/") }, NullLogger<NotifyClient>.Instance);
        await new TicketProcessor(new("operator"), NewRepo(), notify, ai, DbOptions, NullLogger<TicketProcessor>.Instance)
            .HandleAsync(callback, CancellationToken.None);
        await using var check = NewContext();
        var reply = await check.Messages.SingleAsync(m => m.Role == MessageRoles.Assistant);
        Assert.Contains("password", reply.Content);
        Assert.DoesNotContain("Model AI", reply.Content);
        Assert.Equal(1, (await GetJobAsync(readId)).RetryCount);
        Assert.Empty(ai.Requests);
    }
}
