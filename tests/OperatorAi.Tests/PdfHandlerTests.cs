using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

public class PdfHandlerTests : QueueTestBase
{
    private const string Analysis = """
        {"judul":"Analisis","ringkasan":"Ada satu baris duplikat.",
         "temuan_masalah":[{"masalah":"Duplikat","dampak":"Total berlebih"}],
         "rekomendasi_digitalisasi":[{"langkah":"Validasi","manfaat":"Cegah duplikat","prioritas":"tinggi"}],
         "rancangan_tabel":[{"nama":"penjualan","kolom":["id","total"]}],"catatan":"Berdasarkan profil."}
        """;

    private async Task<JobContext> ClaimedPdfAsync()
    {
        var conversation = await NewConversationAsync();
        var policy = Policy();
        await using var db = NewContext();
        var source = new Message { ConversationId = conversation, Role = MessageRoles.User, Content = "buatkan laporan PDF", ModelAlias = TestModel };
        db.Messages.Add(source);
        await db.SaveChangesAsync();
        var analysis = new Job
        {
            ConversationId = conversation, JobType = JobTypes.Analyze, InputJson = PacketJson(), Status = Statuses.Done,
            OutputJson = Analysis, ExecutionPolicyJson = policy.ToJson(), SourceMessageId = source.Id,
        };
        db.Jobs.Add(analysis);
        await db.SaveChangesAsync();
        db.Jobs.Add(new Job
        {
            ConversationId = conversation, JobType = JobTypes.Pdf,
            InputJson = $$"""{"judul":"Laporan: Penjualan/2026","model":"{{TestModel}}"}""",
            DependsOnJobId = analysis.Id, ExecutionPolicyJson = policy.ToJson(), SourceMessageId = source.Id,
        });
        await db.SaveChangesAsync();
        return new JobContext((await NewRepo().ClaimJobAsync("pdf-run", [JobTypes.Pdf]))!, Analysis, policy, source.Id, TestModel);
    }

    [Fact]
    public async Task Retry_after_attachment_commit_reuses_the_same_attachment_and_output_path()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var root = Path.Combine(Path.GetTempPath(), $"OperatorAi_Pdf_{Guid.NewGuid():N}");
        try
        {
            var context = await ClaimedPdfAsync();
            var handler = new PdfHandler(DbOptions, new FileStorage(Options.Create(new StorageOptions { RootPath = root })));
            using var first = JsonDocument.Parse(await handler.HandleAsync(context, CancellationToken.None));
            using var retry = JsonDocument.Parse(await handler.HandleAsync(context, CancellationToken.None));

            Assert.Equal(first.RootElement.GetProperty("attachmentId").GetGuid(), retry.RootElement.GetProperty("attachmentId").GetGuid());
            await using var db = NewContext();
            var attachment = Assert.Single(await db.Attachments.ToListAsync());
            Assert.Equal((context.Job.ConversationId, AttachmentKinds.Output, "application/pdf", "Laporan Penjualan2026.pdf"),
                (attachment.ConversationId, attachment.Kind, attachment.ContentType, attachment.FileName));
            Assert.Equal($"outputs/{context.Job.ConversationId}/{context.Job.Id}.pdf", attachment.StoragePath);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, attachment.StoragePath));
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
            Assert.Equal(bytes.LongLength, attachment.SizeBytes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_recovered_run_cannot_publish_a_PDF_or_attachment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"OperatorAi_Pdf_{Guid.NewGuid():N}");
        var context = await ClaimedPdfAsync();
        await NewRepo().RecoverRunAsync("pdf-run");
        var handler = new PdfHandler(DbOptions, new FileStorage(Options.Create(new StorageOptions { RootPath = root })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(context, CancellationToken.None));
        await using var db = NewContext();
        Assert.Empty(await db.Attachments.ToListAsync());
        Assert.False(Directory.Exists(root));
    }
}
