using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperatorAi.Shared.Contracts;

/// <summary>
/// Hasil analisis dokumen (PDF/gambar) dari job DocumentAnalyze. Skema camelCase tetap:
/// <c>judul</c>, <c>ringkasan</c>, <c>penjelasan</c>, <c>poin</c>, <c>sumberHalaman</c>, <c>catatan</c>,
/// plus <c>finishReason</c> bila panggilan AI terakhir tidak berhenti normal (mis. <c>length</c>).
/// Nomor halaman hanya divalidasi terhadap dokumen oleh handler; <see cref="Parse"/> menjaga skema.
/// </summary>
public sealed record DocumentAnalysisResult(
    [property: JsonPropertyName("judul")] string Judul,
    [property: JsonPropertyName("ringkasan")] string Ringkasan,
    [property: JsonPropertyName("penjelasan")] string Penjelasan,
    [property: JsonPropertyName("poin")] IReadOnlyList<string> Poin,
    [property: JsonPropertyName("sumberHalaman")] IReadOnlyList<int> SumberHalaman,
    [property: JsonPropertyName("catatan")] string Catatan)
{
    /// <summary>
    /// Alasan berhenti penyedia AI (mis. <c>length</c>) bila bukan <c>stop</c>; null tidak ditulis ke output.
    /// Diisi handler dari respons AI, bukan dari JSON model.
    /// </summary>
    [JsonPropertyName("finishReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }

    /// <summary>
    /// Output AI diperlakukan sebagai input tidak tepercaya (DESIGN bagian 16): buang pembungkus blok kode
    /// markdown bila ada, lalu tolak JSON yang tidak sesuai skema dengan <see cref="FormatException"/>.
    /// </summary>
    public static DocumentAnalysisResult Parse(string json)
    {
        var text = string.IsNullOrWhiteSpace(json) ? "" : StripFence(json);
        if (text.Length == 0) throw new FormatException("Output AI kosong; JSON analisis dokumen wajib ada.");

        DocumentAnalysisResult? result;
        try
        {
            result = JsonSerializer.Deserialize<DocumentAnalysisResult>(text, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Path is { Length: > 0 } path
                ? $"JSON analisis dokumen tidak valid pada {path}."
                : "Output AI bukan JSON analisis dokumen yang valid.", ex);
        }

        if (result is null) throw new FormatException("JSON analisis dokumen harus berupa objek.");

        Validate(result);
        return result;
    }

    /// <summary>Teks siap kirim untuk balasan operator (tanpa markdown JSON dan tanpa isi gambar).</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(Judul).Append("\n\n").Append(Ringkasan).Append("\n\n").Append(Penjelasan);
        if (Poin.Count > 0)
        {
            text.Append("\n\nPoin penting:");
            foreach (var poin in Poin) text.Append("\n- ").Append(poin);
        }
        if (SumberHalaman.Count > 0)
            text.Append("\n\nSumber halaman: ").Append(string.Join(", ", SumberHalaman));
        if (!string.IsNullOrWhiteSpace(Catatan))
            text.Append("\n\nCatatan: ").Append(Catatan);
        // Keluaran yang terpotong tidak boleh tampak lengkap; alasan mentah dari penyedia disebut apa adanya.
        if (FinishReason is { Length: > 0 } finish && !finish.Equals("stop", StringComparison.OrdinalIgnoreCase))
            text.Append("\n\nCatatan sistem: keluaran model berhenti karena '").Append(finish).Append("'; hasil ini mungkin tidak lengkap.");
        return text.ToString();
    }

    private static void Validate(DocumentAnalysisResult r)
    {
        RequireText(r.Judul, "judul");
        RequireText(r.Ringkasan, "ringkasan");
        RequireText(r.Penjelasan, "penjelasan");
        // catatan boleh kosong, tetapi tidak boleh hilang/null.
        if (r.Catatan is null) throw new FormatException("Field 'catatan' wajib ada (boleh string kosong).");

        if (r.Poin is null) throw new FormatException("Field 'poin' wajib ada.");
        for (var i = 0; i < r.Poin.Count; i++) RequireText(r.Poin[i], $"poin[{i}]");

        if (r.SumberHalaman is null) throw new FormatException("Field 'sumberHalaman' wajib ada.");
        for (var i = 0; i < r.SumberHalaman.Count; i++)
            if (r.SumberHalaman[i] < 1)
                throw new FormatException($"Field 'sumberHalaman[{i}]' harus nomor halaman minimal 1.");
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException($"Field '{name}' wajib diisi.");
    }

    /// <summary>Buang pembungkus markdown hanya di luar objek; backtick di dalam nilai JSON tetap data.</summary>
    private static string StripFence(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal))
            return text;
        var firstNewline = text.IndexOf('\n');
        return firstNewline < 0 ? text : text[(firstNewline + 1)..^3].Trim();
    }
}
