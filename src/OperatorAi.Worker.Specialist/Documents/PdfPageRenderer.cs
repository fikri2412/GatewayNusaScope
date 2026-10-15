using OperatorAi.Shared.Options;
using OperatorAi.Shared.Storage;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

namespace OperatorAi.Worker.Specialist.Documents;

/// <summary>
/// Render halaman PDF menjadi JPEG lewat PDFium native (PDFtoImage) pada resolusi maksimum dan deadline dari
/// <see cref="DocumentRuntimeLimits"/> snapshot job. PDFium tidak menghormati
/// <see cref="CancellationToken"/> dan bisa menggantung/merusak proses pada PDF cacat, jadi setiap halaman
/// dijaga deadline proses nyata (<see cref="DocumentWatchdog"/>) — bukan klaim pembatalan yang tidak dipatuhi.
/// </summary>
public sealed class PdfPageRenderer(ILogger<PdfPageRenderer> logger)
{
    /// <summary>Halaman 1-based. Mengembalikan JPEG yang sudah dibatasi anggaran <see cref="VisionPayload"/>.</summary>
    public byte[] RenderJpeg(string pdfPath, int pageNumber, DocumentRuntimeLimits limits)
    {
        SKBitmap bitmap;
        try
        {
            bitmap = DocumentWatchdog.Run(TimeSpan.FromSeconds(limits.WatchdogSeconds), $"render halaman {pageNumber}", () =>
            {
                using var stream = File.OpenRead(pdfPath);
#pragma warning disable CA1416 // Worker lokal Windows; bukan runtime browser.
                var size = Conversion.GetPageSize(stream, pageNumber - 1, leaveOpen: true);
                if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
                    throw new DocumentInputException($"Dimensi halaman {pageNumber} tidak valid.");
                var scale = limits.VisionMaxLongEdge / (double)Math.Max(size.Width, size.Height);
                var width = Math.Max(1, (int)Math.Round(size.Width * scale));
                var height = Math.Max(1, (int)Math.Round(size.Height * scale));
                stream.Position = 0;
                return Conversion.ToImage(stream, pageNumber - 1, options: new RenderOptions(Width: width, Height: height));
#pragma warning restore CA1416
            }, logger);
        }
        catch (PdfException ex)
        {
            throw new DocumentInputException($"Halaman {pageNumber} gagal dirender dari PDF yang rusak atau tidak didukung.", ex);
        }

        using (bitmap)
        {
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
                throw new DocumentInputException($"Halaman {pageNumber} tidak menghasilkan gambar.");
            return VisionPayload.EncodeJpeg(bitmap, $"halaman {pageNumber}", limits);
        }
    }
}

/// <summary>
/// Deadline keras untuk panggilan native yang tidak bisa dibatalkan. Worker berjalan satu proses per job, jadi
/// melewati deadline berarti proses keluar dengan kode bukan nol: dispatcher memulihkan job milik run ini dan
/// percobaan ulang mengikuti kebijakan antrean — tidak ada hasil setengah jadi yang ditulis. Batas detiknya
/// datang dari snapshot job (<see cref="DocumentRuntimeLimits.WatchdogSeconds"/>), jadi pembatalan tidak pernah
/// dijanjikan bisa menghentikan panggilan native yang sedang berjalan.
/// </summary>
internal static class DocumentWatchdog
{
    /// <summary>Kode keluar khusus watchdog; apa pun yang bukan nol sudah memicu pemulihan job di dispatcher.</summary>
    public const int ExitCode = 76;

    public static T Run<T>(TimeSpan deadline, string what, Func<T> action, ILogger logger)
    {
        var completed = false;
        using var timer = new Timer(_ =>
        {
            if (Volatile.Read(ref completed)) return;
            logger.LogCritical("Pemrosesan PDF ({What}) melewati {Seconds} detik; proses worker dihentikan", what, deadline.TotalSeconds);
            Environment.Exit(ExitCode);
        }, null, deadline, Timeout.InfiniteTimeSpan);
        try
        {
            var result = action();
            Volatile.Write(ref completed, true);
            return result;
        }
        finally
        {
            Volatile.Write(ref completed, true);
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
