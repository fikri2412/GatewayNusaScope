using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Excel;

namespace OperatorAi.Worker.Specialist.Handlers;

/// <summary>
/// Job ExcelRead (DESIGN bagian 11.1): validasi input, ambil lampiran upload milik percakapan job yang sama,
/// lalu kembalikan profil data file .xlsx/.csv sebagai <c>output_json</c>. Tanpa AI.
/// </summary>
public sealed class ExcelReadHandler(DbContextOptions<AppDbContext> dbOptions, FileStorage storage) : IJobHandler
{
    private static readonly string[] SupportedExtensions = [".xlsx", ".csv"];

    public string JobType => JobTypes.ExcelRead;

    public async Task<string> HandleAsync(JobContext ctx, CancellationToken ct)
    {
        var attachmentId = ReadAttachmentId(ctx.Job.InputJson);
        await using var db = new AppDbContext(dbOptions);
        // Pesan yang sama untuk "tidak ada" dan "milik percakapan lain": keberadaan lampiran lain tidak boleh bocor.
        var attachment = await db.Attachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.ConversationId == ctx.Job.ConversationId, ct)
            ?? throw new InvalidOperationException("Lampiran tidak ditemukan di percakapan ini.");
        if (attachment.Kind != AttachmentKinds.Upload)
            throw new InvalidOperationException("Hanya file yang diupload user yang bisa dianalisis.");
        if (!SupportedExtensions.Contains(Path.GetExtension(attachment.FileName), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Format file tidak didukung (hanya .xlsx dan .csv).");
        if (!ctx.Policy.Upload.AllowedExtensions.Contains(Path.GetExtension(attachment.FileName).ToLowerInvariant()))
            throw new InvalidOperationException("Format file tidak diizinkan oleh kebijakan upload pekerjaan.");

        using var content = storage.OpenRead(attachment.StoragePath);
        if (content.Length > ctx.Policy.Upload.MaxBytes)
            throw new InvalidOperationException($"File melebihi batas upload pekerjaan ({ctx.Policy.Upload.MaxBytes} byte).");
        return ExcelProfiler.Profile(content, attachment.FileName, ct);
    }

    private static Guid ReadAttachmentId(string inputJson)
    {
        ExcelReadInput? input;
        try
        {
            input = JsonSerializer.Deserialize<ExcelReadInput>(inputJson, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Input job ExcelRead bukan JSON yang valid.");
        }
        var id = input?.AttachmentId ?? Guid.Empty;
        if (id == Guid.Empty) throw new InvalidOperationException("attachmentId tidak valid.");
        return id;
    }

    private sealed record ExcelReadInput(Guid? AttachmentId);
}
