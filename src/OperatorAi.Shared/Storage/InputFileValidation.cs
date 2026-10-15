using OperatorAi.Shared.Options;
using SkiaSharp;

namespace OperatorAi.Shared.Storage;

/// <summary>
/// Validasi unggah lampiran dokumen (PDF/PNG/JPEG) yang dipakai API dan worker.
/// <para>
/// PDF di sini hanya diperiksa tanda tangan <c>%PDF-</c>: parser PDF penuh (password/kerusakan/jumlah halaman)
/// berjalan di job DocumentRead yang terisolasi satu proses per job. PNG/JPEG memakai decoder asli (libpng/libjpeg
/// lewat SkiaSharp) untuk format + dimensi, dan hanya didecode penuh setelah dimensinya terbukti ≤ batas piksel
/// efektif <see cref="DocumentRuntimeLimits.MaxImagePixels"/> milik snapshot job — bukan ceiling aplikasi — sehingga
/// gambar besar ditolak sebelum alokasi.
/// </para>
/// </summary>
public static class InputFileValidation
{
    /// <summary>Header PDF boleh didahului sampah kecil; tanda tangan dicari dalam jendela ini.</summary>
    private const int PdfSignatureWindow = 1024;

    public const string Pdf = "application/pdf", Png = "image/png", Jpeg = "image/jpeg", Webp = "image/webp";

    /// <summary>Ekstensi dokumen yang bisa didecode di sini (huruf kecil, dengan titik); sisanya milik job Excel.</summary>
    public static readonly string[] SupportedDocumentExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>Nama MIME kanonik dari ekstensi terverifikasi; deklarasi klien tidak pernah dipercaya.</summary>
    public static string CanonicalContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => Pdf,
        ".png" => Png,
        ".jpg" or ".jpeg" => Jpeg,
        ".webp" => Webp,
        _ => throw new DocumentInputException(
            $"Format file tidak didukung (hanya {string.Join(", ", SupportedDocumentExtensions)})."),
    };

    /// <summary>
    /// Validasi nyata isi file; mengembalikan MIME kanonik atau melempar <see cref="DocumentInputException"/>.
    /// </summary>
    /// <param name="path">Path absolut file yang sudah tersimpan lokal (bukan nama dari user).</param>
    /// <param name="extension">Ekstensi yang diizinkan; penentu decoder, bukan nama file.</param>
    /// <param name="limits">Batas dokumen snapshot job (upload bisa dibuat di bawah kebijakan yang lebih longgar).</param>
    public static Task<string> ValidateAsync(string path, string extension, DocumentRuntimeLimits limits, CancellationToken ct) =>
        Task.Run(() => Validate(path, extension, limits, ct), ct);

    /// <summary>
    /// Format + dimensi gambar dari headernya saja, tanpa decode penuh: jalur job yang akan mendecode sendiri
    /// memakai ini supaya gambar di atas batas piksel ditolak sebelum alokasi.
    /// </summary>
    public static void RequireImageHeader(string path, string extension, int maxImagePixels)
    {
        var (label, format) = ImageFormat(extension);

        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            throw new DocumentInputException($"Isi file bukan gambar {label} yang dapat didecode.");
        if (codec.EncodedFormat != format)
            throw new DocumentInputException($"Isi file bukan gambar {label} sesuai ekstensi {extension.ToLowerInvariant()}.");
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0)
            throw new DocumentInputException("Dimensi gambar tidak valid.");
        if ((long)width * height > maxImagePixels)
            throw new DocumentInputException(
                $"Gambar {width}x{height} melebihi batas {maxImagePixels} piksel.");
    }

    private static string Validate(string path, string extension, DocumentRuntimeLimits limits, CancellationToken ct)
    {
        var contentType = CanonicalContentType(extension);
        if (contentType == Pdf)
        {
            RequirePdfSignature(path);
        }
        else
        {
            RequireImageHeader(path, extension, limits.MaxImagePixels);
            // Decode penuh hanya setelah batas piksel lolos; null = isi rusak walau header terbaca.
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                throw new DocumentInputException(
                    $"Isi file bukan gambar {ImageFormat(extension).Label} yang dapat didecode.");
        }

        ct.ThrowIfCancellationRequested();
        return contentType;
    }

    /// <summary>Label + format Skia yang harus cocok dengan ekstensi; ekstensi non-PNG/WEBP adalah JPEG.</summary>
    private static (string Label, SKEncodedImageFormat Format) ImageFormat(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => ("PNG", SKEncodedImageFormat.Png),
            ".webp" => ("WEBP", SKEncodedImageFormat.Webp),
            _ => ("JPEG", SKEncodedImageFormat.Jpeg),
        };

    private static void RequirePdfSignature(string path)
    {
        using var stream = File.OpenRead(path);
        var size = (int)Math.Min(stream.Length, PdfSignatureWindow);
        var buffer = new byte[size];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (!System.Text.Encoding.ASCII.GetString(buffer, 0, read).Contains("%PDF-", StringComparison.Ordinal))
            throw new DocumentInputException("Isi file bukan PDF yang valid.");
    }
}
