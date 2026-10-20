using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperatorAi.Shared.Contracts;

/// <summary>
/// Output job MermaidRender: lampiran PNG hasil render beserta ringkasan dan catatan/asumsi dari graf sumbernya,
/// supaya balasan operator menyampaikan apa yang belum dijelaskan PRD tanpa membaca ulang graf.
/// <c>attachmentId</c> memakai nama yang sama dengan output PDF, jadi jalur lampiran balasan tidak berubah.
/// </summary>
public sealed record DiagramRenderResult(
    [property: JsonPropertyName("attachmentId")] string AttachmentId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("judul")] string Judul,
    [property: JsonPropertyName("ringkasan")] string Ringkasan,
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("catatan")] IReadOnlyList<string> Catatan)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    /// <summary>Output job dibaca sebagai data tidak tepercaya sampai lolos skema ini; gagal → <see cref="FormatException"/>.</summary>
    public static DiagramRenderResult Parse(string json)
    {
        DiagramRenderResult? result;
        try
        {
            result = JsonSerializer.Deserialize<DiagramRenderResult>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Output job MermaidRender bukan JSON yang valid.", ex);
        }

        if (result is null || !Guid.TryParse(result.AttachmentId, out _) || string.IsNullOrWhiteSpace(result.FileName)
            || string.IsNullOrWhiteSpace(result.Judul) || string.IsNullOrWhiteSpace(result.Ringkasan) || result.Catatan is null)
            throw new FormatException("Output job MermaidRender tidak sesuai skema.");
        return result;
    }

    /// <summary>Teks siap kirim untuk balasan operator; gambar sendiri datang sebagai lampiran pesan.</summary>
    public string ToText()
    {
        var text = new StringBuilder()
            .Append("Diagram alur logika \"").Append(Judul).Append("\" sudah jadi (")
            .Append(NodeCount).Append(" node, ").Append(EdgeCount).Append(" alur) dan bisa dilihat atau diunduh dari lampiran pesan ini.")
            .Append("\n\n").Append(Ringkasan);
        if (Catatan.Count > 0)
        {
            text.Append("\n\nCatatan dan asumsi:");
            foreach (var note in Catatan) text.Append("\n- ").Append(note);
        }

        return text.ToString();
    }
}
