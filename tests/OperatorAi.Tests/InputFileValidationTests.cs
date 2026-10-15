using OperatorAi.Shared.Options;
using OperatorAi.Shared.Storage;
using SkiaSharp;

namespace OperatorAi.Tests;

/// <summary>
/// Validasi unggah bersama 6C: PDF diperiksa tanda tangan <c>%PDF-</c> saja (parser penuh berjalan di job
/// DocumentRead yang terisolasi), PNG/JPEG memakai decoder asli untuk format + batas piksel EFEKTIF dari
/// snapshot job (bukan ceiling aplikasi) dan didecode penuh setelah batas piksel lolos.
/// </summary>
public class InputFileValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OperatorAi_Validation_Test", Guid.NewGuid().ToString("N"));

    /// <summary>Angka kecil yang jelas berbeda dari ceiling aplikasi supaya membuktikan batas policy yang dipakai.</summary>
    private static readonly DocumentRuntimeLimits Limits = DocumentRuntimeLimits.Default with { MaxImagePixels = 4_000_000 };

    private string Write(string name, byte[] content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task Pdf_signature_is_accepted_and_canonical_mime_returned()
    {
        var path = Write("laporan.pdf", DocumentFixtures.TextPdf(1));

        var contentType = await InputFileValidation.ValidateAsync(path, ".pdf", Limits, CancellationToken.None);

        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public async Task Pdf_without_signature_is_rejected()
    {
        var path = Write("bukan.pdf", "teks biasa tanpa tanda tangan PDF"u8.ToArray());

        await Assert.ThrowsAsync<DocumentInputException>(
            () => InputFileValidation.ValidateAsync(path, ".pdf", Limits, CancellationToken.None));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    public async Task Png_jpeg_and_webp_are_decoded_and_report_their_mime(string extension)
    {
        var bytes = extension switch { ".png" => DocumentFixtures.HalvesPng(), ".webp" => DocumentFixtures.HalvesWebp(), _ => DocumentFixtures.HalvesJpeg() };
        var path = Write($"gambar{extension}", bytes);

        var contentType = await InputFileValidation.ValidateAsync(path, extension, Limits, CancellationToken.None);

        Assert.Equal(extension switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" }, contentType);
    }

    [Fact]
    public async Task Content_that_does_not_match_the_extension_is_rejected()
    {
        var path = Write("gambar.jpg", DocumentFixtures.HalvesPng());

        await Assert.ThrowsAsync<DocumentInputException>(
            () => InputFileValidation.ValidateAsync(path, ".jpg", Limits, CancellationToken.None));
    }

    [Fact]
    public async Task Webp_content_under_a_png_extension_is_rejected()
    {
        var path = Write("gambar.png", DocumentFixtures.HalvesWebp());

        await Assert.ThrowsAsync<DocumentInputException>(
            () => InputFileValidation.ValidateAsync(path, ".png", Limits, CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_image_bytes_are_rejected()
    {
        var path = Write("rusak.png", "bukan gambar sama sekali"u8.ToArray());

        await Assert.ThrowsAsync<DocumentInputException>(
            () => InputFileValidation.ValidateAsync(path, ".png", Limits, CancellationToken.None));
    }

    [Fact]
    public async Task Image_over_the_configured_pixel_limit_is_rejected_before_full_decode()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(5000, 4000));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var path = Write("besar.png", data.ToArray());

        var error = await Assert.ThrowsAsync<DocumentInputException>(
            () => InputFileValidation.ValidateAsync(path, ".png", Limits, CancellationToken.None));

        Assert.Contains("4000000 piksel", error.Message);
    }

    [Fact]
    public async Task Image_at_the_configured_pixel_limit_is_accepted()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2000, 2000));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var path = Write("pas-batas.png", data.ToArray());

        Assert.Equal("image/png", await InputFileValidation.ValidateAsync(path, ".png", Limits, CancellationToken.None));
    }

    [Fact]
    public async Task Image_at_the_application_ceiling_is_accepted()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4000, 4000));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var path = Write("pas-ceiling.png", data.ToArray());

        Assert.Equal("image/png", await InputFileValidation.ValidateAsync(
            path, ".png", DocumentRuntimeLimits.Default, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_extension_is_rejected()
    {
        var path = Write("catatan.txt", "teks"u8.ToArray());

        await Assert.ThrowsAsync<DocumentInputException>(
            () => InputFileValidation.ValidateAsync(path, ".txt", Limits, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
