using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Pdf;
using QuestPDF.Fluent;

namespace OperatorAi.Worker.Specialist.Handlers;

public sealed class PdfHandler(DbContextOptions<AppDbContext> dbOptions, FileStorage storage) : IJobHandler
{
    public string JobType => JobTypes.Pdf;

    public async Task<string> HandleAsync(JobContext ctx, CancellationToken ct)
    {
        using var input = JsonDocument.Parse(ctx.Job.InputJson);
        var root = input.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("judul", out var titleProperty)
            || titleProperty.ValueKind != JsonValueKind.String
            || titleProperty.GetString() is not { } title || string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new FormatException("Judul PDF wajib diisi dan maksimal 200 karakter.");
        if (ctx.Job.DependsOnJobId is null || string.IsNullOrWhiteSpace(ctx.DependencyOutput))
            throw new InvalidOperationException("PDF membutuhkan output job analisis.");
        if (root.TryGetProperty("analyzeJobId", out var analyzeId)
            && (analyzeId.ValueKind != JsonValueKind.Number || !analyzeId.TryGetInt64(out var id) || id != ctx.Job.DependsOnJobId))
            throw new FormatException("analyzeJobId tidak sesuai dependensi job PDF.");
        var analysis = AnalysisResult.Parse(ctx.DependencyOutput);
        var relativePath = $"outputs/{ctx.Job.ConversationId}/{ctx.Job.Id}.pdf";
        var outputPath = storage.Resolve(relativePath);
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";

        await using var db = new AppDbContext(dbOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Kunci baris selama publish file: run yang sudah dipulihkan tidak boleh menimpa PDF run baru.
        var held = await db.Jobs.FromSqlInterpolated($"""
            SELECT * FROM jobs WITH (UPDLOCK, ROWLOCK)
            WHERE id = {ctx.Job.Id} AND conversation_id = {ctx.Job.ConversationId}
                AND status = {Statuses.Processing} AND locked_by = {ctx.Job.LockedBy}
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        if (held is null) throw new InvalidOperationException("Job PDF bukan lagi milik run ini.");
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.ConversationId == ctx.Job.ConversationId
            && a.StoragePath == relativePath && a.Kind == AttachmentKinds.Output, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new ReportDocument(title.Trim(), analysis, DateTime.Now).GeneratePdf(file);
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
            if (attachment is null)
            {
                attachment = new Attachment
                {
                    ConversationId = ctx.Job.ConversationId,
                    FileName = CleanFileName(title),
                    StoragePath = relativePath,
                    Kind = AttachmentKinds.Output,
                    ContentType = "application/pdf",
                };
                db.Attachments.Add(attachment);
            }
            attachment.SizeBytes = new FileInfo(outputPath).Length;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return JsonSerializer.Serialize(new { attachmentId = attachment.Id, fileName = attachment.FileName });
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string CleanFileName(string title) => OutputFiles.CleanName(title, ".pdf", "Laporan Analisis Data");
}
