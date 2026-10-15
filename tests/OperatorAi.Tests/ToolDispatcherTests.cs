using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator.Tools;

namespace OperatorAi.Tests;

public class ToolDispatcherTests : QueueTestBase
{
    private const long SourceMessageId = 7;

    private async Task<ToolResult> RunAsync(Guid conversation, string tool, string args,
        ExecutionPolicy? policy = null, long sourceMessageId = SourceMessageId, string model = TestModel)
    {
        await using var db = NewContext();
        return await ToolDispatcher.ExecuteAsync(new ToolContext(db, conversation, policy ?? Policy(), sourceMessageId, model), tool, args, CancellationToken.None);
    }

    private static string Analisis(Guid attachment, string tujuan = "ringkas data", bool pdf = false, bool? langsung = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["attachment_id"] = attachment,
            ["tujuan"] = tujuan,
            ["buat_pdf"] = pdf,
        };
        if (langsung is not null) args["langsung"] = langsung;
        return JsonSerializer.Serialize(args);
    }

    private async Task<List<Job>> JobsAsync()
    {
        await using var db = NewContext();
        return await db.Jobs.AsNoTracking().OrderBy(j => j.Id).ToListAsync();
    }

    [Fact]
    public async Task Unknown_tool_and_malformed_arguments_are_errors_not_exceptions()
    {
        var conversation = await NewConversationAsync();

        var unknown = await RunAsync(conversation, "hapus_semua", "{}");
        Assert.Equal((true, "Tool tidak dikenal."), (unknown.IsError, unknown.Text));
        Assert.True((await RunAsync(conversation, ToolDefinitions.CekStatus, "bukan json")).IsError);
        Assert.True((await RunAsync(conversation, ToolDefinitions.CekStatus, "[1,2]")).IsError);
        Assert.Empty(await JobsAsync());
    }

    [Fact]
    public async Task Analysis_of_an_attachment_from_another_conversation_is_rejected_without_writing_anything()
    {
        var mine = await NewConversationAsync();
        var theirs = await NewConversationAsync();
        var foreign = await NewAttachmentAsync(theirs);

        var result = await RunAsync(mine, ToolDefinitions.AnalisisFile, Analisis(foreign));

        Assert.True(result.IsError);
        Assert.Contains("tidak ditemukan", result.Text);
        Assert.Empty(await JobsAsync());
    }

    [Theory]
    [InlineData("data.pdf", AttachmentKinds.Upload)]       // ekstensi tidak didukung
    [InlineData("data.xlsx", AttachmentKinds.Output)]      // bukan file upload user
    public async Task Analysis_rejects_unsupported_files(string fileName, string kind)
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation, fileName, kind);

        Assert.True((await RunAsync(conversation, ToolDefinitions.AnalisisFile, Analisis(attachment))).IsError);
        Assert.Empty(await JobsAsync());
    }

    [Theory]
    [InlineData("""{"attachment_id":"bukan-guid","tujuan":"x","buat_pdf":false}""")]
    [InlineData("""{"tujuan":"x","buat_pdf":false}""")]
    [InlineData("""{"attachment_id":"00000000-0000-0000-0000-000000000001","tujuan":"   ","buat_pdf":false}""")]
    [InlineData("""{"attachment_id":"00000000-0000-0000-0000-000000000001","tujuan":"x","buat_pdf":"ya"}""")]
    [InlineData("""{"attachment_id":"00000000-0000-0000-0000-000000000001","tujuan":"x","buat_pdf":false,"langsung":"ya"}""")]
    public async Task Analysis_validates_its_arguments(string args)
    {
        var conversation = await NewConversationAsync();

        Assert.True((await RunAsync(conversation, ToolDefinitions.AnalisisFile, args)).IsError);
        Assert.Empty(await JobsAsync());
    }

    [Fact]
    public async Task Analysis_without_pdf_chains_two_jobs_and_notifies_the_operator_after_the_last()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);

        var result = await RunAsync(conversation, ToolDefinitions.AnalisisFile, Analisis(attachment, "cari anomali"));

        Assert.False(result.IsError);
        var jobs = await JobsAsync();
        Assert.Equal([JobTypes.ExcelRead, JobTypes.Analyze], jobs.Select(j => j.JobType));
        Assert.Equal(jobs.Select(j => j.Id), result.NewJobIds);
        Assert.Equal((null, jobs[0].Id), (jobs[0].DependsOnJobId, jobs[1].DependsOnJobId));
        Assert.Equal((false, true), (jobs[0].NotifyOperator, jobs[1].NotifyOperator));
        Assert.Equal(attachment, JsonDocument.Parse(jobs[0].InputJson).RootElement.GetProperty("attachmentId").GetGuid());
        Assert.Equal("cari anomali", JsonDocument.Parse(jobs[1].InputJson).RootElement.GetProperty("tujuan").GetString());
    }

    [Fact]
    public async Task Analysis_with_pdf_moves_the_operator_notification_to_the_pdf_job()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);

        await RunAsync(conversation, ToolDefinitions.AnalisisFile, Analisis(attachment, pdf: true));

        var jobs = await JobsAsync();
        Assert.Equal([JobTypes.ExcelRead, JobTypes.Analyze, JobTypes.Pdf], jobs.Select(j => j.JobType));
        Assert.Equal([null, jobs[0].Id, jobs[1].Id], jobs.Select(j => j.DependsOnJobId));
        Assert.Equal([false, false, true], jobs.Select(j => j.NotifyOperator));
    }

    [Fact]
    public async Task Created_jobs_carry_the_claimed_policy_snapshot_and_the_source_message()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);
        var policy = Policy(maxToolLoops: 9, maxOutputTokens: 321);
        const string model = "model-A";
        long source;
        await using (var db = NewContext())
        {
            var message = new Message { ConversationId = conversation, Role = MessageRoles.User, Content = "analisis", ModelAlias = model };
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            source = message.Id;
        }

        await RunAsync(conversation, ToolDefinitions.AnalisisFile, Analisis(attachment), policy, source, model);

        var jobs = await JobsAsync();
        Assert.All(jobs, job => Assert.Equal((source, policy.ToJson(), model),
            (job.SourceMessageId, job.ExecutionPolicyJson, ModelSnapshot.Read(job.InputJson))));
    }

    [Fact]
    public async Task The_direct_delivery_flag_is_stored_with_the_job_metadata()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);

        await RunAsync(conversation, ToolDefinitions.AnalisisFile, Analisis(attachment, pdf: true, langsung: true));

        var jobs = await JobsAsync();
        Assert.True(JsonDocument.Parse(jobs.Single(j => j.JobType == JobTypes.Analyze).InputJson).RootElement.GetProperty("langsung").GetBoolean());
        Assert.True(JsonDocument.Parse(jobs.Single(j => j.JobType == JobTypes.Pdf).InputJson).RootElement.GetProperty("langsung").GetBoolean());

        // Tanpa flag dari model: metadata bernilai false (konservatif).
        await SetJobAsync(conversation, JobTypes.Analyze, Statuses.Done);
        await RunAsync(conversation, ToolDefinitions.BuatLaporanPdf, """{"judul":"Laporan"}""");
        var pdf = (await JobsAsync()).Where(j => j.JobType == JobTypes.Pdf).OrderBy(j => j.Id).Last();
        Assert.False(JsonDocument.Parse(pdf.InputJson).RootElement.GetProperty("langsung").GetBoolean());
    }

    [Fact]
    public async Task A_failure_while_creating_the_third_job_leaves_no_jobs_behind()
    {
        var conversation = await NewConversationAsync();
        var attachment = await NewAttachmentAsync(conversation);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString)
            .AddInterceptors(new FailOnNthSave(3)).Options;

        await using (var db = new AppDbContext(options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => ToolDispatcher.ExecuteAsync(
                new ToolContext(db, conversation, Policy(), SourceMessageId, TestModel), ToolDefinitions.AnalisisFile, Analisis(attachment, pdf: true), CancellationToken.None));

        Assert.Empty(await JobsAsync());   // dua job pertama ikut dibatalkan
    }

    private sealed class FailOnNthSave(int n) : SaveChangesInterceptor
    {
        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default) =>
            ++_saves == n ? throw new InvalidOperationException("simulasi gagal simpan") : base.SavingChangesAsync(eventData, result, ct);
    }

    [Fact]
    public async Task Pdf_report_needs_a_finished_analysis_from_the_same_conversation()
    {
        var conversation = await NewConversationAsync();
        var other = await NewConversationAsync();
        await SetJobAsync(other, JobTypes.Analyze, Statuses.Done);                    // milik percakapan lain
        await SetJobAsync(conversation, JobTypes.Analyze, Statuses.Processing);       // belum selesai

        var result = await RunAsync(conversation, ToolDefinitions.BuatLaporanPdf, """{"judul":"Laporan"}""");

        Assert.Equal((true, "Belum ada analisis yang selesai."), (result.IsError, result.Text));
        Assert.DoesNotContain(await JobsAsync(), j => j.JobType == JobTypes.Pdf);
    }

    [Fact]
    public async Task Pdf_report_depends_on_the_latest_finished_analysis_and_notifies_the_operator()
    {
        var conversation = await NewConversationAsync();
        await SetJobAsync(conversation, JobTypes.Analyze, Statuses.Done);
        var latestDone = await SetJobAsync(conversation, JobTypes.Analyze, Statuses.Done);
        await SetJobAsync(conversation, JobTypes.Analyze, Statuses.Processing);       // lebih baru tapi belum selesai

        var result = await RunAsync(conversation, ToolDefinitions.BuatLaporanPdf, """{"judul":"Laporan Gudang"}""");

        Assert.False(result.IsError);
        var pdf = (await JobsAsync()).Single(j => j.JobType == JobTypes.Pdf);
        Assert.Equal((latestDone, true, Statuses.Pending), (pdf.DependsOnJobId, pdf.NotifyOperator, pdf.Status));
        var input = JsonDocument.Parse(pdf.InputJson).RootElement;
        Assert.Equal(("Laporan Gudang", latestDone), (input.GetProperty("judul").GetString(), input.GetProperty("analyzeJobId").GetInt64()));
        Assert.Equal([pdf.Id], result.NewJobIds);
    }

    [Fact]
    public async Task Status_lists_only_this_conversations_jobs_in_order()
    {
        var conversation = await NewConversationAsync();
        var other = await NewConversationAsync();

        Assert.Equal("Belum ada pekerjaan di percakapan ini.", (await RunAsync(conversation, ToolDefinitions.CekStatus, "{}")).Text);

        await SetJobAsync(conversation, JobTypes.ExcelRead, Statuses.Done);
        await SetJobAsync(other, JobTypes.Pdf, Statuses.Processing);
        await SetJobAsync(conversation, JobTypes.Analyze, Statuses.Processing);

        Assert.Equal("ExcelRead: Done\nAnalyze: Processing", (await RunAsync(conversation, ToolDefinitions.CekStatus, "{}")).Text);
    }

    private async Task<long> SetJobAsync(Guid conversation, string type, string status)
    {
        await using var db = NewContext();
        var source = new Message { ConversationId = conversation, Role = MessageRoles.User, Content = "job fixture" };
        db.Messages.Add(source);
        await db.SaveChangesAsync();
        // Job aktif wajib membawa snapshot kebijakan + model + pesan sumber (ck_jobs_execution_policy), sama seperti produksi.
        var job = new Job
        {
            ConversationId = conversation, JobType = type, InputJson = PacketJson(), Status = status,
            ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = source.Id,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }
}
