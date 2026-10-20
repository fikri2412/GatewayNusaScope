using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Documents;

namespace OperatorAi.Worker.Specialist.Handlers;

/// <summary>
/// Job DocumentRead: validasi lampiran PDF/gambar milik percakapan job, ekstrak teks per halaman (PdfPig), dan
/// tentukan halaman mana yang perlu diperiksa visual. Tanpa AI dan tanpa gambar: rendering hanya terjadi di
/// DocumentAnalyze, sehingga output job tidak pernah memuat byte/base64/path gambar.
/// </summary>
public sealed class DocumentReadHandler(DbContextOptions<AppDbContext> dbOptions, FileStorage storage,
    ILogger<DocumentReadHandler>? logger = null) : IJobHandler
{
    public string JobType => JobTypes.DocumentRead;

    public async Task<string> HandleAsync(JobContext ctx, CancellationToken ct)
    {
        var input = ReadInput(ctx.Job.InputJson);
        await using var db = new AppDbContext(dbOptions);
        // Pesan yang sama untuk "tidak ada" dan "milik percakapan lain": keberadaan lampiran lain tidak boleh bocor.
        var attachment = await db.Attachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == input.AttachmentId && a.ConversationId == ctx.Job.ConversationId, ct)
            ?? throw new InvalidOperationException("Lampiran tidak ditemukan di percakapan ini.");
        if (attachment.Kind != AttachmentKinds.Upload)
            throw new InvalidOperationException("Hanya file yang diupload user yang bisa dianalisis.");

        // Lampiran bisa dibuat di bawah kebijakan yang lebih longgar: yang berlaku adalah snapshot job ini
        // (bytes + ekstensi), bukan pengaturan gateway terbaru — jalur tool langsung pun tidak bisa melewatinya.
        var upload = ctx.Policy.Upload;
        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (!upload.AllowedExtensions.Contains(extension, StringComparer.Ordinal))
            throw new DocumentInputException(
                $"Format file tidak didukung (hanya {string.Join(", ", upload.AllowedExtensions)}).");
        var path = storage.Resolve(attachment.StoragePath);
        var length = new FileInfo(path).Length;
        if (length > upload.MaxBytes)
            throw new DocumentInputException(
                $"File '{attachment.FileName}' {length} byte melebihi batas {upload.MaxBytes} byte.");
        await InputFileValidation.ValidateAsync(path, extension, ctx.Policy.Document, ct);

        var limits = ctx.Policy.Document;
        var (kind, pages, visualPages, warnings) = extension == ".pdf"
            ? DocumentWatchdog.Run(TimeSpan.FromSeconds(limits.WatchdogSeconds), "ekstraksi teks PDF",
                () => ReadPdf(path, input.Visual, input.TextOnly, limits, ct), logger ?? NullLogger<DocumentReadHandler>.Instance)
            : input.TextOnly
                ? throw new DocumentInputException("Berkas gambar tidak memuat teks yang dapat diekstrak.")
                : ReadImage(attachment.FileName);

        var supportsVision = ModelSnapshot.SupportsVision(ctx.Job.InputJson);
        RequireVision(supportsVision, visualPages, attachment.FileName);

        return DocumentReadOutput.Serialize(new DocumentReadOutput(
            attachment.Id.ToString(), attachment.FileName, kind, pages.Count,
            pages.Select(p => new DocumentPageText(p.PageNumber, p.Text)).ToArray(),
            visualPages,
            pages.Sum(p => p.Text.Length),
            warnings));
    }

    /// <summary>
    /// Halaman perlu diperiksa visual bila tidak memuat teks sama sekali (hasil scan/gambar), memuat
    /// gambar/grafik tertanam (grafik/tabel gambar bisa hilang saat ekstraksi teks), atau user meminta
    /// pemeriksaan visual semua halaman. Halaman dengan teks pendek tetap halaman teks — menandainya visual
    /// akan menolak dokumen teks biasa (halaman judul/pembatas) hanya karena arsitektur model.
    /// </summary>
    private static (string Kind, IReadOnlyList<PdfPageContent> Pages, IReadOnlyList<int> VisualPages, IReadOnlyList<string> Warnings)
        ReadPdf(string path, bool visual, bool textOnly, DocumentRuntimeLimits limits, CancellationToken ct)
    {
        var pages = PdfTextExtractor.Extract(path, limits, ct);
        var visualPages = new List<int>();
        var warnings = new List<string>();
        if (textOnly) return (DocumentReadOutput.KindPdf, pages, [], TextOnlyWarnings(pages));

        if (visual)
        {
            visualPages.AddRange(pages.Select(p => p.PageNumber));
            warnings.Add($"Pemeriksaan visual diminta untuk semua {pages.Count} halaman.");
        }
        else
        {
            foreach (var page in pages)
            {
                if (page.Text.Length == 0)
                {
                    visualPages.Add(page.PageNumber);
                    warnings.Add(page.HasImages
                        ? $"Halaman {page.PageNumber} tidak memuat teks yang dapat diekstrak (memuat gambar/grafik); isinya diperiksa dari gambar halaman."
                        : $"Halaman {page.PageNumber} tidak memuat teks yang dapat diekstrak; isinya diperiksa dari gambar halaman.");
                    continue;
                }
                if (page.HasImages)
                {
                    visualPages.Add(page.PageNumber);
                    warnings.Add($"Halaman {page.PageNumber} memuat gambar/grafik; isinya diperiksa dari gambar halaman.");
                    continue;
                }
            }
        }

        return (DocumentReadOutput.KindPdf, pages, visualPages, warnings);
    }

    /// <summary>File gambar selalu butuh vision: tidak ada teks yang bisa diekstrak dari PNG/JPEG.</summary>
    private static (string Kind, IReadOnlyList<PdfPageContent> Pages, IReadOnlyList<int> VisualPages, IReadOnlyList<string> Warnings)
        ReadImage(string fileName)
    {
        IReadOnlyList<PdfPageContent> pages = [new PdfPageContent(1, "", HasImages: true)];
        return (DocumentReadOutput.KindImage, pages, [1],
            [$"File gambar '{fileName}' tidak memuat teks yang dapat diekstrak; isinya diperiksa secara visual."]);
    }

    /// <summary>
    /// Tanpa dukungan vision, halaman yang hanya bisa dibaca dari gambarnya tidak dapat dianalisis; lebih jujur
    /// gagal sekarang daripada mengirim analisis yang kehilangan isi. Model pengganti tidak pernah dicoba.
    /// </summary>
    private static void RequireVision(bool supportsVision, IReadOnlyList<int> visualPages, string fileName)
    {
        if (visualPages.Count == 0 || supportsVision) return;
        var shown = string.Join(", ", visualPages.Take(10));
        if (visualPages.Count > 10) shown += $", … (+{visualPages.Count - 10} halaman)";
        throw new DocumentInputException(
            $"Halaman {shown} pada '{fileName}' hanya bisa dibaca dari gambarnya, tetapi model yang dipilih tidak mendukung vision. "
            + "Pilih model yang mendukung gambar lalu kirim ulang permintaan.");
    }

    private static (Guid AttachmentId, bool Visual, bool TextOnly) ReadInput(string inputJson)
    {
        ReadInputModel? input;
        try
        {
            input = JsonSerializer.Deserialize<ReadInputModel>(inputJson, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Input job DocumentRead bukan JSON yang valid.");
        }

        var attachmentId = input?.AttachmentId ?? Guid.Empty;
        if (attachmentId == Guid.Empty) throw new InvalidOperationException("attachmentId tidak valid.");
        // visual absen = perilaku bawaan (hanya halaman yang memang perlu); eksplisit true = semua halaman.
        return (attachmentId, input!.Visual ?? false, input.TextOnly ?? false);
    }

    /// <summary>
    /// Mode teks saja (alur logika 6D): hanya teks terekstrak yang dipakai pemanggil, jadi tidak ada halaman visual dan
    /// tidak butuh vision. Halaman yang tidak terbaca tetap dilaporkan di <c>warnings</c> supaya tidak diam-diam hilang.
    /// </summary>
    private static IReadOnlyList<string> TextOnlyWarnings(IReadOnlyList<PdfPageContent> pages)
    {
        var warnings = new List<string>();
        var blank = pages.Where(p => p.Text.Length == 0).Select(p => p.PageNumber).ToList();
        var figures = pages.Where(p => p.Text.Length > 0 && p.HasImages).Select(p => p.PageNumber).ToList();
        if (blank.Count > 0)
            warnings.Add($"Halaman {PageList(blank)} tidak memuat teks yang dapat diekstrak (kemungkinan scan atau gambar) dan tidak dibaca.");
        if (figures.Count > 0)
            warnings.Add($"Halaman {PageList(figures)} memuat gambar/grafik yang tidak dibaca; hanya teksnya yang dipakai.");
        return warnings;
    }

    private static string PageList(IReadOnlyList<int> pages) =>
        string.Join(", ", pages.Take(20)) + (pages.Count > 20 ? $", … (+{pages.Count - 20} halaman)" : "");

    private sealed record ReadInputModel(Guid? AttachmentId, bool? Visual, bool? TextOnly);
}
