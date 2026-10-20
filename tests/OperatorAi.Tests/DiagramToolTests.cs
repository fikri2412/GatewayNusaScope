using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator.Tools;

namespace OperatorAi.Tests;

/// <summary>
/// Tool <c>buat_alur_logika</c> (6D): sumber PRD hanya dari baris pesan/lampiran milik percakapan yang diverifikasi
/// server — teks PRD tidak pernah datang lewat argumen model — dan rantai job dibuat dengan dependensi yang benar.
/// </summary>
public class DiagramToolTests : QueueTestBase
{
    private static readonly string Prd = "PRD pembayaran. " + string.Concat(Enumerable.Repeat("Pengguna memilih metode bayar lalu sistem memeriksa saldo. ", 5));

    private async Task<long> MessageAsync(Guid conversation, string content, string role = MessageRoles.User,
        bool visible = true, bool primary = true)
    {
        await using var db = NewContext();
        var message = new Message
        {
            ConversationId = conversation, Role = role, Content = content, ModelAlias = TestModel, IsVisible = visible, IsPrimary = primary,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    private async Task<ToolResult> RunAsync(Guid conversation, long source, object args)
    {
        await using var db = NewContext();
        return await ToolDispatcher.ExecuteAsync(new ToolContext(db, conversation, Policy(), source, TestModel, SupportsVision: true),
            ToolDefinitions.BuatAlurLogika, JsonSerializer.Serialize(args), CancellationToken.None);
    }

    private async Task<List<Job>> JobsAsync()
    {
        await using var db = NewContext();
        return await db.Jobs.AsNoTracking().OrderBy(j => j.Id).ToListAsync();
    }

    private async Task<Guid> PdfAsync(Guid conversation, string contentType = "application/pdf", string kind = AttachmentKinds.Upload)
    {
        var id = await NewAttachmentAsync(conversation, "prd.pdf", kind);
        await using var db = NewContext();
        await db.Attachments.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContentType, contentType));
        return id;
    }

    private static JsonElement Input(Job job) => JsonDocument.Parse(job.InputJson).RootElement;

    [Fact]
    public async Task A_PRD_in_this_message_creates_plan_then_render_with_the_server_verified_source()
    {
        var conversation = await NewConversationAsync();
        var source = await MessageAsync(conversation, Prd);

        var result = await RunAsync(conversation, source, new { judul = "Alur Pembayaran", tujuan = "Seluruh alur", langsung = true });

        Assert.False(result.IsError);
        var jobs = await JobsAsync();
        Assert.Equal([JobTypes.DiagramPlan, JobTypes.MermaidRender], jobs.Select(j => j.JobType));
        Assert.Equal([null, jobs[0].Id], jobs.Select(j => j.DependsOnJobId));
        Assert.Equal([false, true], jobs.Select(j => j.NotifyOperator));          // hanya hasil akhir yang dikabari ke operator
        Assert.Equal(jobs.Select(j => j.Id), result.NewJobIds);
        Assert.Equal(source, Input(jobs[0]).GetProperty("sourceMessageId").GetInt64());
        Assert.Equal("Seluruh alur", Input(jobs[0]).GetProperty("tujuan").GetString());
        Assert.True(Input(jobs[1]).GetProperty("langsung").GetBoolean());
        Assert.All(jobs, j => Assert.Equal((TestModel, source), (Input(j).GetProperty("model").GetString(), j.SourceMessageId)));
    }

    [Fact]
    public async Task A_short_message_is_not_a_PRD_and_nothing_is_enqueued()
    {
        var conversation = await NewConversationAsync();
        var source = await MessageAsync(conversation, "buatkan alur logikanya");

        var result = await RunAsync(conversation, source, new { judul = "Alur", tujuan = "semua" });

        Assert.True(result.IsError);
        Assert.Contains("sumber_pesan=sebelumnya", result.Text);
        Assert.Empty(await JobsAsync());
    }

    [Fact]
    public async Task The_previous_source_is_the_nearest_earlier_visible_primary_user_message_long_enough_to_be_a_PRD()
    {
        var conversation = await NewConversationAsync();
        var older = await MessageAsync(conversation, "PRD LAMA " + Prd);
        var expected = await MessageAsync(conversation, "PRD BARU " + Prd);
        await MessageAsync(conversation, "ok terima kasih");                                       // terlalu pendek
        await MessageAsync(conversation, "DATA JOB " + Prd, visible: false);                       // baris tersembunyi
        await MessageAsync(conversation, "JAWABAN " + Prd, MessageRoles.Assistant);                // bukan pesan user
        await MessageAsync(conversation, "ALTERNATIF " + Prd, primary: false);                     // jawaban alternatif
        var current = await MessageAsync(conversation, "tolong buatkan alurnya");
        await MessageAsync(conversation, "SETELAH TIKET " + Prd);                                  // lebih baru dari pesan sumber tiket

        var result = await RunAsync(conversation, current, new { judul = "Alur", tujuan = "semua", sumber_pesan = "sebelumnya" });

        Assert.False(result.IsError);
        var plan = (await JobsAsync())[0];
        Assert.Equal(expected, Input(plan).GetProperty("sourceMessageId").GetInt64());
        Assert.NotEqual(older, expected);
    }

    [Fact]
    public async Task Without_an_earlier_PRD_the_previous_source_is_an_error()
    {
        var conversation = await NewConversationAsync();
        await MessageAsync(conversation, "halo");
        var current = await MessageAsync(conversation, "buatkan alurnya");
        var otherConversation = await NewConversationAsync();
        await MessageAsync(otherConversation, Prd);                                                // PRD milik percakapan lain tidak dihitung

        var result = await RunAsync(conversation, current, new { judul = "Alur", tujuan = "semua", sumber_pesan = "sebelumnya" });

        Assert.True(result.IsError);
        Assert.Empty(await JobsAsync());
    }

    [Fact]
    public async Task A_PDF_attachment_creates_a_text_only_read_then_plan_then_render_chain()
    {
        var conversation = await NewConversationAsync();
        var source = await MessageAsync(conversation, "buatkan alur dari PDF ini");
        var pdf = await PdfAsync(conversation);

        var result = await RunAsync(conversation, source, new { judul = "Alur PDF", tujuan = "semua", attachment_id = pdf, sumber_pesan = "sebelumnya" });

        Assert.False(result.IsError);
        var jobs = await JobsAsync();
        Assert.Equal([JobTypes.DocumentRead, JobTypes.DiagramPlan, JobTypes.MermaidRender], jobs.Select(j => j.JobType));
        Assert.Equal([null, jobs[0].Id, jobs[1].Id], jobs.Select(j => j.DependsOnJobId));
        Assert.Equal([false, false, true], jobs.Select(j => j.NotifyOperator));
        var read = Input(jobs[0]);
        Assert.Equal((pdf, false, true), (read.GetProperty("attachmentId").GetGuid(), read.GetProperty("visual").GetBoolean(), read.GetProperty("textOnly").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, Input(jobs[1]).GetProperty("sourceMessageId").ValueKind);   // lampiran menang atas sumber_pesan
    }

    [Theory]
    [InlineData("image/png", AttachmentKinds.Upload)]
    [InlineData("application/pdf", AttachmentKinds.Output)]
    public async Task Only_uploaded_PDFs_are_accepted_as_an_attachment_source(string contentType, string kind)
    {
        var conversation = await NewConversationAsync();
        var source = await MessageAsync(conversation, "buatkan alur");
        var attachment = await PdfAsync(conversation, contentType, kind);

        Assert.True((await RunAsync(conversation, source, new { judul = "Alur", tujuan = "semua", attachment_id = attachment })).IsError);
        Assert.Empty(await JobsAsync());
    }

    [Fact]
    public async Task An_attachment_from_another_conversation_looks_the_same_as_a_missing_one()
    {
        var mine = await NewConversationAsync();
        var source = await MessageAsync(mine, "buatkan alur");
        var foreign = await PdfAsync(await NewConversationAsync());

        var foreignResult = await RunAsync(mine, source, new { judul = "Alur", tujuan = "semua", attachment_id = foreign });
        var missingResult = await RunAsync(mine, source, new { judul = "Alur", tujuan = "semua", attachment_id = Guid.NewGuid() });

        Assert.Equal((true, true), (foreignResult.IsError, missingResult.IsError));
        Assert.Equal(foreignResult.Text, missingResult.Text);
        Assert.Empty(await JobsAsync());
    }

    [Theory]
    [InlineData("""{"tujuan":"semua"}""")]
    [InlineData("""{"judul":"Alur"}""")]
    [InlineData("""{"judul":"Alur","tujuan":"semua","sumber_pesan":"kapan-kapan"}""")]
    [InlineData("""{"judul":"Alur","tujuan":"semua","langsung":"ya"}""")]
    [InlineData("""{"judul":"Alur","tujuan":"semua","attachment_id":"bukan-guid"}""")]
    public async Task Malformed_arguments_are_errors_and_enqueue_nothing(string args)
    {
        var conversation = await NewConversationAsync();
        var source = await MessageAsync(conversation, Prd);
        await using var db = NewContext();

        var result = await ToolDispatcher.ExecuteAsync(new ToolContext(db, conversation, Policy(), source, TestModel),
            ToolDefinitions.BuatAlurLogika, args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(await JobsAsync());
    }

    [Fact]
    public async Task The_tool_is_offered_without_attachments_because_a_PRD_can_be_pasted_text()
    {
        var names = ToolDefinitions.For(fileTools: false).Select(t => (string)t!["function"]!["name"]!).ToList();

        Assert.Contains(ToolDefinitions.BuatAlurLogika, names);
        Assert.DoesNotContain(ToolDefinitions.AnalisisDokumen, names);
        await Task.CompletedTask;
    }
}
