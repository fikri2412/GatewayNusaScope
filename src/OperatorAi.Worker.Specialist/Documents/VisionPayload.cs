using OperatorAi.Shared.Options;
using OperatorAi.Shared.Storage;
using SkiaSharp;

namespace OperatorAi.Worker.Specialist.Documents;

/// <summary>
/// Batas dan encoding gambar untuk permintaan vision ke gateway, semuanya dari <see cref="DocumentRuntimeLimits"/>
/// snapshot job. Anggaran nyata: ≤ <c>VisionMaxImagesPerRequest</c> gambar × ≤ <c>VisionMaxImageBytes</c> JPEG pada
/// resolusi ≤ <c>VisionMaxLongEdge</c> px ⇒ base64 ≤ <c>VisionMaxBase64CharsPerRequest</c> karakter per permintaan;
/// kelayakan itu divalidasi saat policy dibuat, bukan diasumsikan di sini.
/// </summary>
public static class VisionPayload
{
    /// <summary>Perkiraan panjang base64 tanpa mengalokasikan stringnya.</summary>
    public static long Base64Length(int bytes) => 4L * ((bytes + 2) / 3);

    /// <summary>
    /// Encode JPEG di atas latar putih (JPEG tanpa alpha) sambil menurunkan resolusi bertahap sampai muat anggaran.
    /// Isi gambar tidak pernah dibuang; bila bahkan resolusi minimum tidak muat, pemanggil mendapat error jelas.
    /// </summary>
    public static byte[] EncodeJpeg(SKBitmap source, string what, DocumentRuntimeLimits limits)
    {
        using var image = SKImage.FromBitmap(source);
        var longEdge = Math.Max(source.Width, source.Height);
        var edge = Math.Min(longEdge, limits.VisionMaxLongEdge);
        var minimum = Math.Min(edge, limits.VisionMinLongEdge);
        var step = Math.Max(1, limits.VisionMaxLongEdge / 10);
        for (; ; edge = Math.Max(minimum, edge - step))
        {
            var width = Math.Max(1, (int)Math.Round((double)source.Width * edge / longEdge));
            var height = Math.Max(1, (int)Math.Round((double)source.Height * edge / longEdge));
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height));
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, edge >= limits.VisionMaxLongEdge ? 80 : 75);
            if (data.Size <= limits.VisionMaxImageBytes) return data.ToArray();
            if (edge == minimum) break;
        }

        throw new DocumentInputException(
            $"Gambar {what} tetap melebihi {limits.VisionMaxImageBytes} byte pada resolusi minimum {limits.VisionMinLongEdge}px.");
    }

}
