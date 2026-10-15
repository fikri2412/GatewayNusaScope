using OperatorAi.Shared.Contracts;

namespace OperatorAi.Tests;

/// <summary>
/// Skema output analisis dokumen (kontrak bersama Operator ↔ worker): parse sebagai input tidak tepercaya,
/// nama field camelCase tetap, dan finishReason hanya muncul bila model tidak berhenti normal.
/// </summary>
public class DocumentAnalysisResultTests
{
    private const string Valid =
        """{"judul":"Judul","ringkasan":"Ringkasan","penjelasan":"Penjelasan","poin":["a","b"],"sumberHalaman":[1,2],"catatan":"catatan"}""";

    [Fact]
    public void Parse_reads_the_camel_case_schema_and_strips_markdown_fences()
    {
        var result = DocumentAnalysisResult.Parse("```json\n" + Valid + "\n```");

        Assert.Equal("Judul", result.Judul);
        Assert.Equal("Ringkasan", result.Ringkasan);
        Assert.Equal("Penjelasan", result.Penjelasan);
        Assert.Equal(new[] { "a", "b" }, result.Poin);
        Assert.Equal(new[] { 1, 2 }, result.SumberHalaman);
        Assert.Equal("catatan", result.Catatan);
        Assert.Null(result.FinishReason);
    }

    [Theory]
    [InlineData("""{"ringkasan":"r","penjelasan":"p","poin":[],"sumberHalaman":[],"catatan":""}""")]                    // judul hilang
    [InlineData("""{"judul":"j","penjelasan":"p","poin":[],"sumberHalaman":[],"catatan":""}""")]                        // ringkasan hilang
    [InlineData("""{"judul":"j","ringkasan":"r","penjelasan":" ","poin":[],"sumberHalaman":[],"catatan":""}""")]        // penjelasan kosong
    [InlineData("""{"judul":"j","ringkasan":"r","penjelasan":"p","sumberHalaman":[],"catatan":""}""")]                   // poin hilang
    [InlineData("""{"judul":"j","ringkasan":"r","penjelasan":"p","poin":[],"catatan":""}""")]                           // sumberHalaman hilang
    [InlineData("""{"judul":"j","ringkasan":"r","penjelasan":"p","poin":[],"sumberHalaman":[0],"catatan":""}""")]       // halaman 0
    [InlineData("""{"judul":"j","ringkasan":"r","penjelasan":"p","poin":[],"sumberHalaman":[]}""")]                     // catatan hilang
    public void Parse_rejects_output_that_violates_the_schema(string json)
    {
        Assert.Throws<FormatException>(() => DocumentAnalysisResult.Parse(json));
    }

    [Fact]
    public void Parse_rejects_empty_output()
    {
        Assert.Throws<FormatException>(() => DocumentAnalysisResult.Parse("   "));
    }


}
