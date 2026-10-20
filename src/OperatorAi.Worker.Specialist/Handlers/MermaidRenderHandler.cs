using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Diagrams;

namespace OperatorAi.Worker.Specialist.Handlers;

/// <summary>
/// Job MermaidRender (6D, tanpa AI): graf tervalidasi dari DiagramPlan → sumber Mermaid buatan aplikasi → PNG lokal →
/// lampiran output <c>outputs/{conversationId}/{jobId}.png</c>. Render (hingga puluhan detik) berjalan di luar transaksi;
/// hanya publish file + baris lampiran yang memegang kunci baris job, sehingga heartbeat lease tidak terblokir dan run
/// yang sudah dipulihkan tidak menimpa output run baru. Retry memakai lampiran dan path yang sama.
/// </summary>
public sealed class MermaidRenderHandler(IMermaidRenderer renderer, DbContextOptions<AppDbContext> dbOptions, FileStorage storage,
    ILogger<MermaidRenderHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.MermaidRender;

    public async Task<string> HandleAsync(JobContext ctx, CancellationToken ct)
    {
        var title = ReadTitle(ctx.Job.InputJson);
        if (ctx.Job.DependsOnJobId is null || string.IsNullOrWhiteSpace(ctx.DependencyOutput))
            throw new InvalidOperationException("Job MermaidRender membutuhkan output job DiagramPlan yang sudah Done.");
        // Output dependensi dibuat AI: divalidasi ulang di sini, bukan dipercaya karena sudah lewat satu job.
        var graph = DiagramGraph.Parse(ctx.DependencyOutput);

        var png = await renderer.RenderPngAsync(MermaidFlowchart.Serialize(graph), ct);
        logger.LogInformation("Diagram dirender: nodes={Nodes} edges={Edges} bytes={Bytes}", graph.Nodes.Count, graph.Edges.Count, png.Length);

        var relativePath = $"outputs/{ctx.Job.ConversationId}/{ctx.Job.Id}.png";
        var outputPath = storage.Resolve(relativePath);
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp";

        await using var db = new AppDbContext(dbOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var held = await db.Jobs.FromSqlInterpolated($"""
            SELECT * FROM jobs WITH (UPDLOCK, ROWLOCK)
            WHERE id = {ctx.Job.Id} AND conversation_id = {ctx.Job.ConversationId}
                AND status = {Statuses.Processing} AND locked_by = {ctx.Job.LockedBy}
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        if (held is null) throw new InvalidOperationException("Job MermaidRender bukan lagi milik run ini.");
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.ConversationId == ctx.Job.ConversationId
            && a.StoragePath == relativePath && a.Kind == AttachmentKinds.Output, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(temporaryPath, png, ct);
            File.Move(temporaryPath, outputPath, overwrite: true);
            if (attachment is null)
            {
                attachment = new Attachment
                {
                    ConversationId = ctx.Job.ConversationId,
                    FileName = OutputFiles.CleanName(title, ".png", "Diagram Alur Logika"),
                    StoragePath = relativePath,
                    Kind = AttachmentKinds.Output,
                    ContentType = "image/png",
                };
                db.Attachments.Add(attachment);
            }

            attachment.SizeBytes = png.Length;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new DiagramRenderResult(attachment.Id.ToString(), attachment.FileName, graph.Judul, graph.Ringkasan,
                graph.Nodes.Count, graph.Edges.Count, graph.Catatan).ToJson();
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ReadTitle(string inputJson)
    {
        try
        {
            using var input = JsonDocument.Parse(inputJson);
            if (input.RootElement.ValueKind == JsonValueKind.Object
                && input.RootElement.TryGetProperty("judul", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } title && !string.IsNullOrWhiteSpace(title) && title.Length <= DiagramPlanHandler.MaxJudulChars)
                return title.Trim();
        }
        catch (JsonException)
        {
            // jatuh ke pesan format di bawah
        }

        throw new FormatException($"Judul diagram wajib diisi dan maksimal {DiagramPlanHandler.MaxJudulChars} karakter.");
    }
}
