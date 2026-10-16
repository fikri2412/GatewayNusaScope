using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using OperatorAi.Worker.Specialist.Excel;

namespace OperatorAi.Tests;

/// <summary>
/// Profil ExcelRead (DESIGN bagian 11.1): tiga file contoh di <c>tests/samples</c> plus batas CSV (kutip,
/// newline, BOM, header kosong/duplikat, tanpa header, baris duplikat, cap 10.000 baris).
/// </summary>
public class ExcelProfilerTests
{
    private static string SamplesDir() => Path.Combine(AppContext.BaseDirectory, "samples");

    private static JsonElement ProfileSample(string fileName)
    {
        using var content = File.OpenRead(Path.Combine(SamplesDir(), fileName));
        return ParseProfile(ExcelProfiler.Profile(content, fileName));
    }

    private static JsonElement ProfileText(string text, string fileName)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return ParseProfile(ExcelProfiler.Profile(content, fileName));
    }

    private static JsonElement ProfileWorkbook(Action<IXLWorkbook> build, string fileName = "workbook.xlsx")
    {
        using IXLWorkbook workbook = new XLWorkbook();
        build(workbook);
        using var content = new MemoryStream();
        workbook.SaveAs(content);
        content.Position = 0;
        return ParseProfile(ExcelProfiler.Profile(content, fileName));
    }

    private static JsonElement ParseProfile(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Sheet(JsonElement profile, int index = 0) => profile.GetProperty("sheets")[index];

    private static JsonElement Column(JsonElement sheet, string name) =>
        sheet.GetProperty("columns").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    private static string[] Names(JsonElement sheet) =>
        sheet.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString() ?? "").ToArray();

    [Fact]
    public void Tidy_xlsx_is_profiled_with_header_in_first_row()
    {
        var profile = ProfileSample("rapi.xlsx");

        Assert.Equal("rapi.xlsx", profile.GetProperty("fileName").GetString());
        Assert.Empty(profile.GetProperty("catatan").EnumerateArray());
        var sheet = Sheet(profile);
        Assert.Equal("Penjualan", sheet.GetProperty("name").GetString());
        Assert.Equal(1, sheet.GetProperty("headerRow").GetInt32());
        Assert.Equal(8, sheet.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, sheet.GetProperty("duplicateRows").GetInt32());
        Assert.Equal(6, sheet.GetProperty("columns").GetArrayLength());

        var tanggal = Column(sheet, "Tanggal");
        Assert.Equal("date", tanggal.GetProperty("type").GetString());
        Assert.Equal(0, tanggal.GetProperty("empty").GetInt32());
        Assert.Equal(6, tanggal.GetProperty("unique").GetInt32());
        Assert.Equal("2026-01-05", tanggal.GetProperty("min").GetString());
        Assert.Equal("2026-03-20", tanggal.GetProperty("max").GetString());
        Assert.Equal(5, tanggal.GetProperty("examples").GetArrayLength());

        var jumlah = Column(sheet, "Jumlah");
        Assert.Equal("number", jumlah.GetProperty("type").GetString());
        Assert.Equal(4d, jumlah.GetProperty("min").GetDouble());
        Assert.Equal(25d, jumlah.GetProperty("max").GetDouble());
        Assert.Equal(13.125, jumlah.GetProperty("avg").GetDouble(), 6);

        var disetujui = Column(sheet, "Disetujui");
        Assert.Equal("bool", disetujui.GetProperty("type").GetString());
        Assert.Equal(2, disetujui.GetProperty("unique").GetInt32());
        Assert.Equal(new[] { true, false }, disetujui.GetProperty("examples").EnumerateArray().Select(e => e.GetBoolean()).ToArray());

        var catatan = Column(sheet, "Catatan");
        Assert.Equal("text", catatan.GetProperty("type").GetString());
        Assert.Equal(3, catatan.GetProperty("empty").GetInt32());
        Assert.Equal(3, catatan.GetProperty("unique").GetInt32());
        Assert.False(catatan.TryGetProperty("min", out _));
        Assert.False(catatan.TryGetProperty("avg", out _));

        var samples = sheet.GetProperty("sampleRows");
        Assert.Equal(5, samples.GetArrayLength());
        Assert.Equal("2026-01-05", samples[0].GetProperty("Tanggal").GetString());
        Assert.Equal("Kopi Arabika", samples[0].GetProperty("Barang").GetString());
        Assert.Equal(10d, samples[0].GetProperty("Jumlah").GetDouble());
        Assert.True(samples[0].GetProperty("Disetujui").GetBoolean());
        Assert.Equal(JsonValueKind.Null, samples[1].GetProperty("Catatan").ValueKind);
    }

    [Fact]
    public void Xlsx_with_title_and_blank_rows_has_header_in_third_row()
    {
        var sheet = Sheet(ProfileSample("header-baris-3.xlsx"));

        Assert.Equal("Laporan", sheet.GetProperty("name").GetString());
        Assert.Equal(3, sheet.GetProperty("headerRow").GetInt32());
        Assert.Equal(6, sheet.GetProperty("rowCount").GetInt32());
        Assert.Equal(1, sheet.GetProperty("duplicateRows").GetInt32());
        Assert.Equal(5, sheet.GetProperty("sampleRows").GetArrayLength()); // 6 baris data, contoh maksimal 5
        Assert.Equal(new[] { "Tanggal", "Produk", "Terjual", "Pendapatan" }, Names(sheet));

        var terjual = Column(sheet, "Terjual");
        Assert.Equal("number", terjual.GetProperty("type").GetString());
        Assert.Equal(2d, terjual.GetProperty("min").GetDouble());
        Assert.Equal(8d, terjual.GetProperty("max").GetDouble());
        Assert.Equal("2026-01-10", Column(sheet, "Tanggal").GetProperty("min").GetString());
    }

    [Fact]
    public void Semicolon_csv_with_quotes_newlines_and_bom_is_parsed_as_rows()
    {
        var sheet = Sheet(ProfileSample("pemisah-titik-koma.csv"));

        Assert.Equal("pemisah-titik-koma", sheet.GetProperty("name").GetString());
        Assert.Equal(1, sheet.GetProperty("headerRow").GetInt32());
        Assert.Equal(4, sheet.GetProperty("rowCount").GetInt32());
        Assert.Equal(0, sheet.GetProperty("duplicateRows").GetInt32());
        Assert.Equal(new[] { "Tanggal", "Produk", "Jumlah", "Harga", "Catatan" }, Names(sheet)); // BOM tidak ikut jadi nama

        var produk = Column(sheet, "Produk");
        Assert.Equal("text", produk.GetProperty("type").GetString());
        Assert.Equal(3, produk.GetProperty("unique").GetInt32());
        var contoh = produk.GetProperty("examples").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("Kopi; Arabika", contoh);
        Assert.Contains("Teh \"Premium\"", contoh);

        var harga = Column(sheet, "Harga");
        Assert.Equal("number", harga.GetProperty("type").GetString());
        Assert.Equal(30000.5, harga.GetProperty("min").GetDouble(), 6);
        Assert.Equal(95000d, harga.GetProperty("max").GetDouble(), 6);
        Assert.Equal(65500.125, harga.GetProperty("avg").GetDouble(), 6);

        var catatan = Column(sheet, "Catatan");
        Assert.Equal(1, catatan.GetProperty("empty").GetInt32());
        Assert.Equal(2, catatan.GetProperty("unique").GetInt32());

        var samples = sheet.GetProperty("sampleRows");
        Assert.Equal("Teh \"Premium\"", samples[1].GetProperty("Produk").GetString());
        Assert.Equal("dikirim\nbesok", samples[1].GetProperty("Catatan").GetString());
    }

    [Fact]
    public void Csv_blank_and_duplicate_headers_are_renamed_without_losing_columns()
    {
        var sheet = Sheet(ProfileText("Nama;;Nama;Nama\nBudi;x;1;y;tambahan\nSari;y;2;z\n", "data.csv"));

        Assert.Equal(new[] { "Nama", "kolom_2", "Nama_2", "Nama_3", "kolom_5" }, Names(sheet));
        Assert.Equal(2, sheet.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public void Csv_without_detectable_header_falls_back_without_fabricating_one()
    {
        var sheet = Sheet(ProfileText("1;2\n3;4\n\n5;6\n", "angka.csv"));

        Assert.Equal(0, sheet.GetProperty("headerRow").GetInt32());
        Assert.Equal(new[] { "kolom_1", "kolom_2" }, Names(sheet));
        Assert.Equal(3, sheet.GetProperty("rowCount").GetInt32());

        var kolom1 = Column(sheet, "kolom_1");
        Assert.Equal("number", kolom1.GetProperty("type").GetString());
        Assert.Equal(1d, kolom1.GetProperty("min").GetDouble());
        Assert.Equal(5d, kolom1.GetProperty("max").GetDouble());
        Assert.Equal(3, kolom1.GetProperty("avg").GetDouble(), 6);
    }

    [Fact]
    public void Repeated_rows_are_all_counted_as_duplicates()
    {
        var sheet = Sheet(ProfileText("a;1\r\nb;2\r\na;1\r\na;1\r\n", "dup.csv")); // sekaligus menguji baris CRLF

        Assert.Equal(4, sheet.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, sheet.GetProperty("duplicateRows").GetInt32());
    }

    [Fact]
    public void Mixed_column_uses_dominant_type_and_empty_column_is_marked_empty()
    {
        var sheet = Sheet(ProfileText("Campuran;Kosong\n1;\n2;\n3;\n4;\n", "campur.csv"));

        var campuran = Column(sheet, "Campuran");
        Assert.Equal("number", campuran.GetProperty("type").GetString());
        Assert.Equal(4, campuran.GetProperty("unique").GetInt32());

        var kosong = Column(sheet, "Kosong");
        Assert.Equal("empty", kosong.GetProperty("type").GetString());
        Assert.Equal(4, kosong.GetProperty("empty").GetInt32());
        Assert.Equal(0, kosong.GetProperty("unique").GetInt32());
        Assert.Equal(0, kosong.GetProperty("examples").GetArrayLength());
    }

    [Fact]
    public void Unique_count_is_capped_at_ten_thousand_rows()
    {
        var csv = new StringBuilder("Nilai\n");
        for (var i = 1; i <= 10_050; i++) csv.Append(i).Append('\n');

        var sheet = Sheet(ProfileText(csv.ToString(), "besar.csv"));
        Assert.Equal(10_050, sheet.GetProperty("rowCount").GetInt32());
        var nilai = Column(sheet, "Nilai");
        Assert.Equal(10_000, nilai.GetProperty("unique").GetInt32());
        Assert.Equal(10_050d, nilai.GetProperty("max").GetDouble(), 6);
    }

    [Fact]
    public void Sheet_and_column_limits_are_reported_in_notes()
    {
        var profile = ProfileWorkbook(workbook =>
        {
            var wide = workbook.AddWorksheet("Wide");
            for (var c = 1; c <= 105; c++) wide.Cell(1, c).Value = $"K{c}";
            var keep = workbook.AddWorksheet("Keep");
            keep.Cell(1, 1).Value = "Nama";
            keep.Cell(2, 1).Value = "A";
            for (var i = 0; i < 10; i++) workbook.AddWorksheet($"Extra{i}");
        }, "banyak.xlsx");

        Assert.Equal(10, profile.GetProperty("sheets").GetArrayLength());
        var wide = Sheet(profile);
        Assert.Equal(100, wide.GetProperty("columns").GetArrayLength());
    }

    [Fact]
    public void Numbers_and_dates_parse_invariantly_under_other_machine_cultures()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var sheet = Sheet(ProfileText("Nilai;Tanggal\n1.5;2026-01-02\n2.5;2026-01-03\n", "culture.csv"));

            var nilai = Column(sheet, "Nilai");
            Assert.Equal("number", nilai.GetProperty("type").GetString());
            Assert.Equal(1.5, nilai.GetProperty("min").GetDouble(), 6);
            Assert.Equal(2d, nilai.GetProperty("avg").GetDouble(), 6);
            Assert.Equal("2026-01-02", Column(sheet, "Tanggal").GetProperty("min").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Short_rows_and_late_columns_count_missing_cells_as_empty()
    {
        var sheet = Sheet(ProfileText("A;B\n1\n2;3;4\n5;6\n", "ragged.csv"));
        Assert.Equal(1, Column(sheet, "B").GetProperty("empty").GetInt32());
        Assert.Equal(2, Column(sheet, "kolom_3").GetProperty("empty").GetInt32());
        Assert.Equal(3, sheet.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public void Column_limit_does_not_make_distinct_wide_rows_look_like_duplicates()
    {
        var header = string.Join(';', Enumerable.Range(1, 101).Select(i => $"K{i}"));
        var common = string.Join(';', Enumerable.Repeat("1", 100));
        var sheet = Sheet(ProfileText($"{header}\n{common};2\n{common};3\n{common};2\n", "wide.csv"));
        Assert.Equal(100, sheet.GetProperty("columns").GetArrayLength());
        Assert.Equal(1, sheet.GetProperty("duplicateRows").GetInt32());
    }

    [Fact]
    public void Typed_values_remain_distinct_and_the_dominant_column_type_wins()
    {
        var sheet = Sheet(ProfileWorkbook(workbook =>
        {
            var ws = workbook.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Nilai";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(3, 1).Value = "1";
            ws.Cell(4, 1).Value = 1;
        }));
        var value = Column(sheet, "Nilai");
        Assert.Equal("number", value.GetProperty("type").GetString());
        Assert.Equal(2, value.GetProperty("unique").GetInt32());
        Assert.Equal(1, sheet.GetProperty("duplicateRows").GetInt32());
    }

    [Theory]
    [InlineData("Nama;Nilai\n\"tidak ditutup;1")]
    [InlineData("Nama;Nilai\n\"nama\"ekstra;1")]
    public void Malformed_quoted_CSV_is_rejected_instead_of_silently_changing_data(string csv) =>
        Assert.Throws<FormatException>(() => ProfileText(csv, "broken.csv"));

    [Fact]
    public void Decimal_comma_is_not_silently_reinterpreted_as_a_thousands_separator()
    {
        var sheet = Sheet(ProfileText("Nilai;Nama\n1,5;A\n2,5;B\n", "localized.csv"));
        var value = Column(sheet, "Nilai");
        Assert.Equal("text", value.GetProperty("type").GetString());
        Assert.Equal("1,5", value.GetProperty("examples")[0].GetString());
    }

    [Fact]
    public void A_sparse_sheet_with_a_far_corner_cell_is_rejected_instead_of_scanning_billions_of_cells()
    {
        // Dua sel (A1 dan XFD1048576) = file kecil, tetapi baris x kolom yang dialamati ~1,7e10.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<OperatorAi.Shared.Storage.DocumentInputException>(() => ProfileWorkbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("jarang");
            sheet.Cell(1, 1).Value = "a";
            sheet.Cell(XLHelper.MaxRowNumber, XLHelper.MaxColumnNumber).Value = "z";
        }));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "penolakan harus segera, bukan setelah memindai sheet");
    }

    [Fact]
    public void An_archive_that_expands_past_the_safety_limit_is_rejected_before_it_is_parsed()
    {
        using var content = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(content, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", System.IO.Compression.CompressionLevel.Fastest).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 210; i++) entry.Write(zeros); // ~210 MB terurai, beberapa ratus KB terkompresi
        }
        content.Position = 0;
        Assert.True(content.Length < 5 * 1024 * 1024);

        Assert.Throws<OperatorAi.Shared.Storage.DocumentInputException>(() => ExcelProfiler.Profile(content, "bomb.xlsx"));
    }
}
