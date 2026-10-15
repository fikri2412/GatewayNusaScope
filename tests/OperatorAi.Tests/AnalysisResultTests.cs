using OperatorAi.Shared.Contracts;

namespace OperatorAi.Tests;

public class AnalysisResultTests
{
    private static string Json(
        string judul = "\"Judul\"",
        string ringkasan = "\"Ringkasan\"",
        string temuanMasalah = """[{"masalah":"Masalah","dampak":"Dampak"}]""",
        string rekomendasi = """[{"langkah":"Langkah","manfaat":"Manfaat","prioritas":"sedang"}]""",
        string rancanganTabel = """[{"nama":"Penjualan","kolom":["Tanggal","Total"]}]""",
        string catatan = "\"Catatan\"") =>
        $$"""
        {"judul":{{judul}},"ringkasan":{{ringkasan}},"temuan_masalah":{{temuanMasalah}},"rekomendasi_digitalisasi":{{rekomendasi}},"rancangan_tabel":{{rancanganTabel}},"catatan":{{catatan}}}
        """;

    [Fact]
    public void Valid_object_parses_every_field()
    {
        var result = AnalysisResult.Parse(Json());

        Assert.Equal("Judul", result.Judul);
        Assert.Equal("Ringkasan", result.Ringkasan);
        Assert.Equal("Catatan", result.Catatan);
        var temuan = Assert.Single(result.TemuanMasalah);
        Assert.Equal(("Masalah", "Dampak"), (temuan.Masalah, temuan.Dampak));
        var rekomendasi = Assert.Single(result.RekomendasiDigitalisasi);
        Assert.Equal(("Langkah", "Manfaat", "sedang"), (rekomendasi.Langkah, rekomendasi.Manfaat, rekomendasi.Prioritas));
        var tabel = Assert.Single(result.RancanganTabel);
        Assert.Equal("Penjualan", tabel.Nama);
        Assert.Equal(["Tanggal", "Total"], tabel.Kolom);
    }

    [Fact]
    public void Empty_arrays_and_empty_catatan_are_allowed()
    {
        var result = AnalysisResult.Parse(Json(temuanMasalah: "[]", rekomendasi: "[]", rancanganTabel: "[]", catatan: "\"\""));

        Assert.Empty(result.TemuanMasalah);
        Assert.Empty(result.RekomendasiDigitalisasi);
        Assert.Empty(result.RancanganTabel);
        Assert.Equal("", result.Catatan);
    }

    [Fact]
    public void Markdown_wrapper_is_stripped_without_changing_backticks_in_JSON_values()
    {
        var json = Json(judul: "\"Gunakan ``` untuk contoh kode\"");
        Assert.Equal("Gunakan ``` untuk contoh kode", AnalysisResult.Parse(json).Judul);
        Assert.Equal("Gunakan ``` untuk contoh kode", AnalysisResult.Parse("```json\n" + json + "\n```").Judul);
    }

    public static TheoryData<string> Invalid => new()
    {
        Json(judul: "null"),
        Json(judul: "\"  \""),
        Json(judul: "123"),
        Json(ringkasan: "null"),
        """{"ringkasan":"r","temuan_masalah":[],"rekomendasi_digitalisasi":[],"rancangan_tabel":[],"catatan":""}""",
        Json(temuanMasalah: "null"),
        Json(rekomendasi: "null"),
        Json(rancanganTabel: "null"),
        Json(catatan: "null"),
        """{"judul":"j","ringkasan":"r","temuan_masalah":[],"rekomendasi_digitalisasi":[],"rancangan_tabel":[]}""",
        Json(temuanMasalah: """[{"masalah":"","dampak":"d"}]"""),
        Json(temuanMasalah: """[{"masalah":"m"}]"""),
        Json(temuanMasalah: "[null]"),
        Json(temuanMasalah: "{}"),
        Json(rekomendasi: """[{"langkah":"l","manfaat":"m","prioritas":1}]"""),
        Json(rekomendasi: """[{"langkah":"l","manfaat":"m","prioritas":"Tinggi"}]"""),
        Json(rekomendasi: """[{"langkah":"l","manfaat":"m","prioritas":null}]"""),
        Json(rekomendasi: """[{"langkah":"l","manfaat":" ","prioritas":"rendah"}]"""),
        Json(rancanganTabel: """[{"nama":"t","kolom":["a",""]}]"""),
        Json(rancanganTabel: """[{"nama":"t"}]"""),
        "bukan json",
        "```json\n{\"judul\":\"j\"}\n```",
        "",
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_content_is_rejected(string json)
    {
        Assert.Throws<FormatException>(() => AnalysisResult.Parse(json));
    }

}
