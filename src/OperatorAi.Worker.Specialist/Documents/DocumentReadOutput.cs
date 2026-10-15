using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperatorAi.Worker.Specialist.Documents;

/// <summary>
/// Output job DocumentRead: teks per halaman + halaman mana yang perlu diperiksa visual. Tidak pernah memuat
/// gambar, base64, atau path storage — DocumentAnalyze mengambil byte gambar langsung dari lampiran aslinya.
/// </summary>
public sealed record DocumentReadOutput(
    [property: JsonPropertyName("attachmentId")] string AttachmentId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("pages")] IReadOnlyList<DocumentPageText> Pages,
    [property: JsonPropertyName("visualPages")] IReadOnlyList<int> VisualPages,
    [property: JsonPropertyName("extractedChars")] int ExtractedChars,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings)
{
    public const string KindPdf = "pdf", KindImage = "image";

    public static string Serialize(DocumentReadOutput output) => JsonSerializer.Serialize(output, JsonSerializerOptions.Web);

    /// <summary>
    /// Output dependensi diperlakukan sebagai kontrak internal yang harus konsisten: halaman 1..N masing-masing
    /// sekali, halaman visual bagian dari halaman itu. Pelanggaran = bug integrasi, bukan input user.
    /// </summary>
    public static DocumentReadOutput Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Output job DocumentRead kosong.");

        DocumentReadOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<DocumentReadOutput>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Output job DocumentRead bukan JSON yang valid.", ex);
        }

        if (output is null) throw new InvalidOperationException("Output job DocumentRead harus berupa objek.");
        if (!Guid.TryParse(output.AttachmentId, out _))
            throw new InvalidOperationException("Output job DocumentRead tanpa attachmentId yang valid.");
        if (string.IsNullOrWhiteSpace(output.FileName))
            throw new InvalidOperationException("Output job DocumentRead tanpa nama file.");
        if (output.Kind is not (KindPdf or KindImage))
            throw new InvalidOperationException($"Output job DocumentRead memakai kind '{output.Kind}' yang tidak dikenal.");
        if (output.PageCount < 1 || output.Pages is null || output.Pages.Count != output.PageCount)
            throw new InvalidOperationException(
                $"Output job DocumentRead tidak konsisten: pageCount {output.PageCount} dengan {output.Pages?.Count} halaman.");
        for (var i = 0; i < output.Pages.Count; i++)
            if (output.Pages[i] is null || output.Pages[i].PageNumber != i + 1 || output.Pages[i].Text is null)
                throw new InvalidOperationException("Output job DocumentRead memuat daftar halaman yang tidak berurutan 1..N.");
        if (output.VisualPages is null)
            throw new InvalidOperationException("Output job DocumentRead tanpa daftar visualPages.");

        var visual = new List<int>(output.VisualPages.Count);
        foreach (var page in output.VisualPages)
        {
            if (page < 1 || page > output.PageCount)
                throw new InvalidOperationException(
                    $"Output job DocumentRead menandai halaman {page} di luar 1..{output.PageCount}.");
            if (!visual.Contains(page)) visual.Add(page);
        }

        visual.Sort();
        if (output.Kind == KindImage && (output.PageCount != 1 || visual.Count != 1))
            throw new InvalidOperationException("Output job DocumentRead untuk file gambar harus satu halaman yang diperiksa visual.");
        return output with { VisualPages = visual };
    }
}

/// <summary>Teks hasil ekstraksi satu halaman; <c>pageNumber</c> 1-based sesuai dokumen aslinya.</summary>
public sealed record DocumentPageText(
    [property: JsonPropertyName("pageNumber")] int PageNumber,
    [property: JsonPropertyName("text")] string Text);
