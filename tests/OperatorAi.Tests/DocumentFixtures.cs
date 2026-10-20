using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace OperatorAi.Tests;

/// <summary>
/// Fixture dokumen nyata untuk regresi 6C: PDF dibuat QuestPDF (teks + gambar tertanam), gambar dibuat SkiaSharp,
/// PDF terkunci dirakit manual (QuestPDF tidak menulis enkripsi). Semua byte dihasilkan di sini, tanpa file biner
/// tambahan.
/// </summary>
internal static class DocumentFixtures
{
    static DocumentFixtures() => QuestPDF.Settings.License = LicenseType.Community;

    public static readonly SKColor Red = new(0xFF, 0x00, 0x00);
    public static readonly SKColor Blue = new(0x00, 0x00, 0xFF);

    /// <summary>Gambar dua warna kiri/kanan untuk membuktikan isi halaman benar-benar sampai ke model.</summary>
    public static byte[] HalvesPng(int width = 400, int height = 300) => Encode(Halves(width, height), SKEncodedImageFormat.Png, 100);

    public static byte[] HalvesJpeg(int width = 400, int height = 300) => Encode(Halves(width, height), SKEncodedImageFormat.Jpeg, 90);

    public static byte[] HalvesWebp(int width = 400, int height = 300) => Encode(Halves(width, height), SKEncodedImageFormat.Webp, 90);

    /// <summary>PDF teks murni: setiap halaman memuat penanda yang bisa dicari per halaman.</summary>
    public static byte[] TextPdf(int pages, string marker = "HALAMAN")
    {
        using var stream = new MemoryStream();
        Document.Create(document =>
        {
            for (var i = 1; i <= pages; i++)
                document.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Content().Text($"{marker}-{i} isi teks halaman {i}");
                });
        }).GeneratePdf(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// PDF campuran: <paramref name="textPages"/> halaman teks lalu <paramref name="imagePages"/> halaman hanya
    /// gambar (setara scan, tanpa teks). Gambar tanpa margin supaya isi halaman pasti terwarnai penuh.
    /// </summary>
    public static byte[] MixedPdf(int textPages = 1, int imagePages = 1)
    {
        var png = HalvesPng();
        using var stream = new MemoryStream();
        Document.Create(document =>
        {
            for (var i = 1; i <= textPages; i++)
                document.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Content().Text($"HALAMAN-SATU isi teks halaman {i}");
                });
            for (var i = 0; i < imagePages; i++)
                document.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(0);
                    page.Content().Image(png).FitUnproportionally();
                });
        }).GeneratePdf(stream);
        return stream.ToArray();
    }

    /// <summary>PDF satu halaman berisi teks dan gambar sekaligus (PRD bertext yang menyisipkan bagan).</summary>
    public static byte[] TextWithFigurePdf()
    {
        var png = HalvesPng();
        using var stream = new MemoryStream();
        Document.Create(document =>
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Content().Column(column =>
                {
                    column.Item().Text("ALUR-PRD pengguna memilih metode bayar");
                    column.Item().Height(200).Image(png).FitArea();
                });
            })).GeneratePdf(stream);
        return stream.ToArray();
    }

    /// <summary>PDF satu halaman dengan teks sangat pendek (di bawah ambang) dan tanpa gambar.</summary>
    public static byte[] ShortTextPdf()
    {
        using var stream = new MemoryStream();
        Document.Create(document =>
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Content().Text("Judul");
            })).GeneratePdf(stream);
        return stream.ToArray();
    }

    /// <summary>PDF satu halaman hanya gambar (tanpa teks) — kasus hasil scan.</summary>
    public static byte[] ScannedPdf() => MixedPdf(textPages: 0);

    /// <summary>
    /// PDF satu halaman hanya gambar acak (deterministik, sulit dimampatkan): anggaran byte JPEG jadi benar-benar
    /// mengikat, berbeda dari halaman dua warna yang selalu kecil.
    /// </summary>
    public static byte[] NoisePdf(int width = 400, int height = 300)
    {
        var png = Encode(Noise(width, height), SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream();
        Document.Create(document =>
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.Content().Image(png).FitUnproportionally();
            })).GeneratePdf(stream);
        return stream.ToArray();
    }

    public static byte[] CorruptPdf() => Encoding.ASCII.GetBytes("%PDF-1.4\nini bukan PDF yang bisa dibaca");

    /// <summary>
    /// PDF minimal dengan /Encrypt RC4 40-bit dan password pemilik/user yang tidak diketahui: PdfPig harus menolak
    /// dengan <c>PdfDocumentEncryptedException</c>, bukan membuka isinya.
    /// </summary>
    public static byte[] EncryptedPdf()
    {
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Object(int number, string body)
        {
            offsets.Add(builder.Length);
            builder.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>");
        Object(4, "<< /Filter /Standard /V 1 /R 2 /O <" + new string('A', 64) + "> /U <" + new string('B', 64) + "> /P -1 >>");

        var xref = builder.Length;
        builder.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R /ID [<")
            .Append(new string('C', 32)).Append("> <").Append(new string('C', 32)).Append(">] >>\n");
        builder.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static SKBitmap Halves(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Red);
        canvas.DrawRect(new SKRect(width / 2f, 0, width, height), new SKPaint { Color = Blue });
        return bitmap;
    }

    /// <summary>Piksel acak ber-seed tetap: fixture sama tiap run, tapi hampir tidak bisa dimampatkan JPEG.</summary>
    private static SKBitmap Noise(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height));
        var random = new Random(6);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(format, quality))
            return data.ToArray();
    }
}
