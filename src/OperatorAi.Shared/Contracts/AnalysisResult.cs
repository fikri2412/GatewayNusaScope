using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperatorAi.Shared.Contracts;

/// <summary>
/// Hasil analisis AI (DESIGN bagian 11.2) — kontrak bersama AnalyzeHandler → ReportDocument/PdfHandler dan balasan
/// langsung Operator (tanpa AI) untuk laporan yang sudah lengkap.
/// Urutan prioritas rekomendasi: <see cref="AnalysisResult.PrioritasTinggi"/> → <see cref="AnalysisResult.PrioritasSedang"/>
/// → <see cref="AnalysisResult.PrioritasRendah"/>.
/// </summary>
public sealed record AnalysisResult(
    [property: JsonPropertyName("judul")] string Judul,
    [property: JsonPropertyName("ringkasan")] string Ringkasan,
    [property: JsonPropertyName("temuan_masalah")] IReadOnlyList<TemuanMasalah> TemuanMasalah,
    [property: JsonPropertyName("rekomendasi_digitalisasi")] IReadOnlyList<RekomendasiDigitalisasi> RekomendasiDigitalisasi,
    [property: JsonPropertyName("rancangan_tabel")] IReadOnlyList<RancanganTabel> RancanganTabel,
    [property: JsonPropertyName("catatan")] string Catatan)
{
    public const string PrioritasTinggi = "tinggi";
    public const string PrioritasSedang = "sedang";
    public const string PrioritasRendah = "rendah";

    /// <summary>
    /// Output AI diperlakukan sebagai input tidak tepercaya (DESIGN bagian 16): buang pembungkus blok kode
    /// markdown (tiga backtick) bila ada, lalu tolak JSON yang tidak sesuai skema dengan <see cref="FormatException"/>.
    /// </summary>
    public static AnalysisResult Parse(string json)
    {
        var text = string.IsNullOrWhiteSpace(json) ? "" : StripFence(json);
        if (text.Length == 0) throw new FormatException("Output AI kosong; JSON analisis wajib ada.");

        AnalysisResult? result;
        try
        {
            result = JsonSerializer.Deserialize<AnalysisResult>(text, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Path is { Length: > 0 } path
                ? $"JSON analisis tidak valid pada {path}."
                : "Output AI bukan JSON analisis yang valid.", ex);
        }

        if (result is null) throw new FormatException("JSON analisis harus berupa objek.");

        Validate(result);
        return result;
    }

    private static void Validate(AnalysisResult r)
    {
        RequireText(r.Judul, "judul");
        RequireText(r.Ringkasan, "ringkasan");
        // catatan boleh kosong (PdfHandler melewatkannya), tetapi tidak boleh hilang/null.
        if (r.Catatan is null) throw new FormatException("Field 'catatan' wajib ada (boleh string kosong).");

        if (r.TemuanMasalah is null) throw new FormatException("Field 'temuan_masalah' wajib ada.");
        for (var i = 0; i < r.TemuanMasalah.Count; i++)
        {
            var temuan = r.TemuanMasalah[i];
            if (temuan is null) throw new FormatException($"Field 'temuan_masalah[{i}]' tidak boleh null.");
            RequireText(temuan.Masalah, $"temuan_masalah[{i}].masalah");
            RequireText(temuan.Dampak, $"temuan_masalah[{i}].dampak");
        }

        if (r.RekomendasiDigitalisasi is null) throw new FormatException("Field 'rekomendasi_digitalisasi' wajib ada.");
        for (var i = 0; i < r.RekomendasiDigitalisasi.Count; i++)
        {
            var rekomendasi = r.RekomendasiDigitalisasi[i];
            if (rekomendasi is null) throw new FormatException($"Field 'rekomendasi_digitalisasi[{i}]' tidak boleh null.");
            RequireText(rekomendasi.Langkah, $"rekomendasi_digitalisasi[{i}].langkah");
            RequireText(rekomendasi.Manfaat, $"rekomendasi_digitalisasi[{i}].manfaat");
            if (rekomendasi.Prioritas is not (PrioritasTinggi or PrioritasSedang or PrioritasRendah))
                throw new FormatException($"Field 'rekomendasi_digitalisasi[{i}].prioritas' harus tinggi, sedang, atau rendah.");
        }

        if (r.RancanganTabel is null) throw new FormatException("Field 'rancangan_tabel' wajib ada.");
        for (var i = 0; i < r.RancanganTabel.Count; i++)
        {
            var tabel = r.RancanganTabel[i];
            if (tabel is null) throw new FormatException($"Field 'rancangan_tabel[{i}]' tidak boleh null.");
            RequireText(tabel.Nama, $"rancangan_tabel[{i}].nama");
            if (tabel.Kolom is null) throw new FormatException($"Field 'rancangan_tabel[{i}].kolom' wajib ada.");
            for (var j = 0; j < tabel.Kolom.Count; j++)
                RequireText(tabel.Kolom[j], $"rancangan_tabel[{i}].kolom[{j}]");
        }
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

public sealed record TemuanMasalah(
    [property: JsonPropertyName("masalah")] string Masalah,
    [property: JsonPropertyName("dampak")] string Dampak);

public sealed record RekomendasiDigitalisasi(
    [property: JsonPropertyName("langkah")] string Langkah,
    [property: JsonPropertyName("manfaat")] string Manfaat,
    [property: JsonPropertyName("prioritas")] string Prioritas);

public sealed record RancanganTabel(
    [property: JsonPropertyName("nama")] string Nama,
    [property: JsonPropertyName("kolom")] IReadOnlyList<string> Kolom);
