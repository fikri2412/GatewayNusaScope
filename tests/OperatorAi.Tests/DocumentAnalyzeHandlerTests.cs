using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Documents;
using OperatorAi.Worker.Specialist.Handlers;
using SkiaSharp;

namespace OperatorAi.Tests;

/// <summary>
/// Job DocumentAnalyze (6C): halaman bergambar diperiksa lewat gambar asli (bukan klaim), teks penuh tetap
/// dipakai untuk sintesis, nomor halaman digroundkan ke dokumen, dan outputnya selalu sesuai skema bersama.
/// </summary>
public class DocumentAnalyzeHandlerTests : DocumentTestBase
{
    private const string ValidOnPageOne =
        """{"judul":"Laporan","ringkasan":"Ringkasan","penjelasan":"Penjelasan","poin":["Poin satu"],"sumberHalaman":[1],"catatan":""}""";

    private const string InventedPage =
        """{"judul":"Laporan","ringkasan":"Ringkasan","penjelasan":"Penjelasan","poin":[],"sumberHalaman":[7],"catatan":""}""";

    /// <summary>Batas vision yang dikonfigurasi di snapshot job; jelas berbeda dari default 1280/640/400000.</summary>
    private const int MaxEdge = 256, MinEdge = 128, MaxImageBytes = 40_000;
    private const string DataUrl = "data:image/jpeg;base64,";

    private static DocumentRuntimeLimits VisualLimits =>
        DocumentRuntimeLimits.Default with
        {
            VisionMaxLongEdge = MaxEdge, VisionMinLongEdge = MinEdge, VisionMaxImageBytes = MaxImageBytes,
        };

    private DocumentAnalyzeHandler Handler(IAiClient ai) => new(ai, DbOptions, Storage,
        new PdfPageRenderer(NullLogger<PdfPageRenderer>.Instance), NullLogger<DocumentAnalyzeHandler>.Instance);

    private static AiResponse Answer(string text, string? finish = null, int prompt = 100, int completion = 20) =>
        new(text, finish, prompt, completion, new AiMessage("assistant", text), true);

    /// <summary>Output dependensi dihasilkan oleh DocumentRead asli supaya Analyze diuji terhadap kontrak nyata.</summary>
    private async Task<string> ReadAsync(Guid conversationId, Guid attachmentId, bool visual = false)
    {
        var context = await NewJobContextAsync(conversationId, JobTypes.DocumentRead, ReadInput(attachmentId, visual));
        return await new DocumentReadHandler(DbOptions, Storage).HandleAsync(context, CancellationToken.None);
    }

    private static byte[] FirstImageJpeg(AiRequest request)
    {
        var parts = request.Messages[0].ContentParts;
        Assert.NotNull(parts);
        var url = parts![1]!["image_url"]!["url"]!.GetValue<string>();
        Assert.StartsWith(DataUrl, url);
        return Convert.FromBase64String(url[DataUrl.Length..]);
    }

    /// <summary>Total karakter base64 gambar dalam satu permintaan vision (yang dibandingkan dengan anggaran policy).</summary>
    private static long TotalBase64Chars(AiRequest request) =>
        request.Messages[0].ContentParts!.Skip(1)
            .Sum(part => (long)part!["image_url"]!["url"]!.GetValue<string>().Length - DataUrl.Length);

    [Fact]
    public async Task A_text_pdf_is_synthesised_from_the_original_goal_without_any_visual_call()
    {
        const string goal = "Tolong hitung total anggaran dan jelaskan tabelnya.";
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "laporan.pdf", DocumentFixtures.TextPdf(2));
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read, goal: goal);

        var output = await Handler(ai).HandleAsync(context, CancellationToken.None);

        var request = Assert.Single(ai.Requests);
        Assert.Null(request.Messages[0].ContentParts);                     // dokumen teks tidak mengirim gambar
        Assert.Contains(goal, request.Messages[0].Content);                // permintaan asli user, bukan hanya fokus tool
        Assert.Contains("HALAMAN-1", request.Messages[0].Content);         // teks penuh ikut ke sintesis
        Assert.Equal(TestModel, request.Model);
        Assert.DoesNotContain(goal, request.System);                       // tujuan user tidak pernah jadi instruksi tepercaya
        Assert.Equal(120, context.Tokens.Total);                           // semua usage panggilan tercatat
        Assert.DoesNotContain("base64", output);
        Assert.Equal(1, DocumentAnalysisResult.Parse(output).SumberHalaman[0]);
    }

    [Fact]
    public async Task A_scanned_page_is_rendered_and_sent_as_a_decodable_jpeg()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "campuran.pdf", DocumentFixtures.MixedPdf());
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(
            _ => Answer("Halaman 2: terlihat bidang merah dan biru."),
            _ => Answer("""{"judul":"L","ringkasan":"R","penjelasan":"P","poin":[],"sumberHalaman":[2],"catatan":""}"""));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read,
            policy: Policy(document: VisualLimits));

        var output = await Handler(ai).HandleAsync(context, CancellationToken.None);

        Assert.Equal(2, ai.Requests.Count);                                // satu batch visual + satu sintesis
        var visual = ai.Requests[0];
        Assert.False(visual.Messages[0].IsPlainText);
        var text = visual.Messages[0].ContentParts![0]!["text"]!.GetValue<string>();
        Assert.Contains("halaman 2", text);
        Assert.Contains("tidak ada teks yang dapat diekstrak", text);      // halaman 2 memang tanpa teks
        Assert.Single(visual.Messages[0].ContentParts!.Skip(1));           // hanya halaman 2, tidak menambah/membuang
        var jpeg = FirstImageJpeg(visual);
        Assert.True(jpeg.Length <= MaxImageBytes, $"jpeg {jpeg.Length} byte");
        using (var bitmap = SKBitmap.Decode(jpeg))
        {
            Assert.NotNull(bitmap);
            Assert.Equal(MaxEdge, Math.Max(bitmap.Width, bitmap.Height));  // dirender pada sisi panjang policy, bukan default
            var left = bitmap.GetPixel(bitmap.Width / 4, bitmap.Height / 2);
            var right = bitmap.GetPixel(bitmap.Width * 3 / 4, bitmap.Height / 2);
            Assert.True(left.Red > 180 && left.Blue < 90, $"kiri={left}");  // gambar halaman benar-benar ter-render
            Assert.True(right.Blue > 180 && right.Red < 90, $"kanan={right}");
        }
        Assert.Null(ai.Requests[1].Messages[0].ContentParts);              // sintesis hanya teks + catatan
        Assert.Contains("Halaman 2: terlihat bidang merah dan biru.", ai.Requests[1].Messages[0].Content);
        Assert.Contains("HALAMAN-SATU", ai.Requests[1].Messages[0].Content); // teks penuh dokumen tetap dipakai
        Assert.DoesNotContain("data:image", output);                       // tidak ada gambar/base64 di output job
        Assert.Equal(new[] { 2 }, DocumentAnalysisResult.Parse(output).SumberHalaman);
    }

    [Theory]
    [InlineData("gambar.png")]
    [InlineData("gambar.jpg")]
    [InlineData("gambar.webp")]
    public async Task A_standalone_image_is_normalised_and_sent_within_the_vision_budget(string fileName)
    {
        var conversation = await NewConversationAsync();
        var bytes = fileName.EndsWith(".png") ? DocumentFixtures.HalvesPng() : fileName.EndsWith(".webp") ? DocumentFixtures.HalvesWebp() : DocumentFixtures.HalvesJpeg();
        var attachmentId = await UploadAsync(conversation, fileName, bytes);
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(
            _ => Answer("Gambar: separuh merah, separuh biru."),
            _ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read,
            policy: Policy(document: VisualLimits));

        var output = await Handler(ai).HandleAsync(context, CancellationToken.None);

        Assert.Equal(2, ai.Requests.Count);
        var jpeg = FirstImageJpeg(ai.Requests[0]);
        Assert.True(jpeg.Length <= MaxImageBytes, $"jpeg {jpeg.Length} byte");
        using (var bitmap = SKBitmap.Decode(jpeg))
        {
            Assert.NotNull(bitmap);
            Assert.Equal((MaxEdge, MaxEdge * 300 / 400), (bitmap.Width, bitmap.Height)); // sumber 400x300 dinormalkan ke sisi panjang policy
            var left = bitmap.GetPixel(bitmap.Width / 4, bitmap.Height / 2);
            var right = bitmap.GetPixel(bitmap.Width * 3 / 4, bitmap.Height / 2);
            Assert.True(left.Red > 180 && left.Blue < 90, $"kiri={left}");
            Assert.True(right.Blue > 180 && right.Red < 90, $"kanan={right}");
        }
        Assert.Equal("Laporan", DocumentAnalysisResult.Parse(output).Judul);
    }

    [Fact]
    public async Task Visual_pages_without_a_vision_model_fail_before_any_ai_call()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "scan.pdf", DocumentFixtures.ScannedPdf());
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze,
            AnalyzeInput(supportsVision: false), read);

        var error = await Assert.ThrowsAsync<DocumentInputException>(
            () => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Contains("tidak mendukung vision", error.Message);
        Assert.Empty(ai.Requests);
        Assert.Equal(0, context.Tokens.Total);
    }

    [Fact]
    public async Task A_page_that_cannot_be_rendered_fails_instead_of_being_dropped()
    {
        var conversation = await NewConversationAsync();
        // Lampiran berekstensi .pdf berisi PNG: DocumentRead akan menolaknya, di sini dependency output disuntik
        // untuk membuktikan Analyze tidak diam-diam melewati halaman visual yang gagal dirender.
        var attachmentId = await UploadAsync(conversation, "palsu.pdf", DocumentFixtures.HalvesPng());
        var read = DocumentReadOutput.Serialize(new DocumentReadOutput(attachmentId.ToString(), "palsu.pdf",
            DocumentReadOutput.KindPdf, 1, [new DocumentPageText(1, "teks")], [1], 4, []));
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);

        var error = await Assert.ThrowsAsync<DocumentInputException>(
            () => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Contains("gagal dirender", error.Message);
        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task An_invented_page_number_is_repaired_once_with_the_previous_answer()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "campuran.pdf", DocumentFixtures.MixedPdf());
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(
            _ => Answer("Catatan halaman 2."),
            _ => Answer(InventedPage),
            _ => Answer("""{"judul":"L","ringkasan":"R","penjelasan":"P","poin":[],"sumberHalaman":[2],"catatan":""}"""));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);

        var output = await Handler(ai).HandleAsync(context, CancellationToken.None);

        Assert.Equal(3, ai.Requests.Count);
        Assert.Equal(DocumentAnalyzeHandler.RepairInstruction, ai.Requests[2].Messages[^1].Content);
        Assert.Equal(InventedPage, ai.Requests[2].Messages[1].Content);     // jawaban sebelumnya ikut dikirim ulang
        Assert.Equal(new[] { 2 }, DocumentAnalysisResult.Parse(output).SumberHalaman);
    }

    [Fact]
    public async Task Still_invalid_after_the_single_repair_fails_the_job()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "campuran.pdf", DocumentFixtures.MixedPdf());
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer("catatan"), _ => Answer(InventedPage), _ => Answer(InventedPage));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);

        await Assert.ThrowsAsync<FormatException>(() => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Equal(3, ai.Requests.Count);
    }

    [Fact]
    public async Task A_truncated_final_answer_keeps_the_finish_reason_instead_of_claiming_completeness()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "laporan.pdf", DocumentFixtures.TextPdf(1));
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne, finish: "length"));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);

        var output = await Handler(ai).HandleAsync(context, CancellationToken.None);

        var result = DocumentAnalysisResult.Parse(output);
        Assert.Equal("length", result.FinishReason);
        Assert.Contains("\"finishReason\":\"length\"", output);
        Assert.Contains("tidak lengkap", result.ToText());
    }

    [Fact]
    public async Task Truncated_visual_notes_cannot_be_presented_as_a_complete_final_report()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "scan.pdf", DocumentFixtures.ScannedPdf());
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer("Halaman 1: sebagian terlihat", "length"), _ => Answer(ValidOnPageOne, "stop"));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);
        var result = DocumentAnalysisResult.Parse(await Handler(ai).HandleAsync(context, CancellationToken.None));
        Assert.Equal("length", result.FinishReason);
    }

    [Fact]
    public async Task Model_json_cannot_override_the_transport_finish_reason()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "teks.pdf", DocumentFixtures.TextPdf(1));
        var read = await ReadAsync(conversation, attachmentId);
        var forged = ValidOnPageOne[..^1] + ",\"finishReason\":\"length\"}";
        var ai = new ScriptedAi(_ => Answer(forged, "stop"));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);
        var result = DocumentAnalysisResult.Parse(await Handler(ai).HandleAsync(context, CancellationToken.None));
        Assert.Null(result.FinishReason);
    }

    [Fact]
    public async Task Every_visual_page_is_covered_across_a_batch_boundary()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "scan-lima.pdf", DocumentFixtures.MixedPdf(0, 5));
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(
            _ => Answer("Catatan halaman 1-4."),
            _ => Answer("PENANDA-HALAMAN-LIMA"),
            _ => Answer("""{"judul":"L","ringkasan":"R","penjelasan":"P","poin":[],"sumberHalaman":[5],"catatan":""}"""));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);
        await Handler(ai).HandleAsync(context, CancellationToken.None);
        Assert.Equal(3, ai.Requests.Count);
        Assert.Equal(5, ai.Requests[0].Messages[0].ContentParts!.Count);
        Assert.Equal(2, ai.Requests[1].Messages[0].ContentParts!.Count);
        Assert.Contains("PENANDA-HALAMAN-LIMA", ai.Requests[2].Messages[0].Content);
    }

    [Fact]
    public async Task A_configured_batch_size_splits_visual_calls_without_dropping_pages()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "scan-lima.pdf", DocumentFixtures.MixedPdf(0, 5));
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer("Catatan visual."), _ => Answer(ValidOnPageOne));
        var policy = Policy(document: VisualLimits with { VisionMaxImagesPerRequest = 2 });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read, policy: policy);

        await Handler(ai).HandleAsync(context, CancellationToken.None);

        var visual = ai.Requests.Where(r => r.Messages[0].ContentParts is not null).ToArray();
        Assert.Equal(3, visual.Length);                                    // 2+2+1 halaman, bukan satu batch default 4
        Assert.Equal(new[] { 3, 3, 2 }, visual.Select(r => r.Messages[0].ContentParts!.Count).ToArray());
        Assert.Equal(5, visual.Sum(r => r.Messages[0].ContentParts!.Count - 1));
    }

    [Fact]
    public async Task Visual_batches_stay_within_the_configured_base64_budget()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "scan-lima.pdf", DocumentFixtures.MixedPdf(0, 5));
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer("Catatan visual."), _ => Answer(ValidOnPageOne));
        // Anggaran base64 paling ketat yang masih memuat satu JPEG maksimum (divalidasi saat policy dibuat).
        var policy = Policy(document: DocumentRuntimeLimits.Default with
        {
            VisionMaxLongEdge = 128, VisionMinLongEdge = 128, VisionMaxImageBytes = 4_000,
            VisionMaxBase64CharsPerRequest = 5_336,
        });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read, policy: policy);

        await Handler(ai).HandleAsync(context, CancellationToken.None);

        var visual = ai.Requests.Where(r => r.Messages[0].ContentParts is not null).ToArray();
        Assert.NotEmpty(visual);
        foreach (var request in visual)
            Assert.True(TotalBase64Chars(request) <= 5_336, $"base64 {TotalBase64Chars(request)} karakter");
        Assert.Equal(5, visual.Sum(r => r.Messages[0].ContentParts!.Count - 1));   // tidak ada halaman yang dibuang
    }

    [Fact]
    public async Task A_page_that_cannot_fit_the_min_edge_budget_fails_instead_of_shrinking_forever()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "noise.pdf", DocumentFixtures.NoisePdf(45, 64));
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        // Noise seukuran render minimum A4: downsample gambar besar justru meratakan noise dan bisa muat 1 KiB.
        var policy = Policy(document: DocumentRuntimeLimits.Default with
        {
            VisionMaxLongEdge = 64, VisionMinLongEdge = 64, VisionMaxImageBytes = 1_024,
            VisionMaxBase64CharsPerRequest = 1_368,
        });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read, policy: policy);

        await Assert.ThrowsAsync<DocumentInputException>(
            () => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task A_standalone_image_over_the_job_snapshot_pixel_limit_is_rejected_before_decoding()
    {
        var conversation = await NewConversationAsync();
        using var bitmap = new SKBitmap(new SKImageInfo(1000, 1000));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var attachmentId = await UploadAsync(conversation, "besar.png", data.ToArray());
        var read = await ReadAsync(conversation, attachmentId);            // lolos kebijakan saat diunggah
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var policy = Policy(document: DocumentRuntimeLimits.Default with { MaxImagePixels = 500_000 });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read, policy: policy);

        var error = await Assert.ThrowsAsync<DocumentInputException>(
            () => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Contains("500000 piksel", error.Message);
        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task The_conversation_token_budget_is_checked_before_any_call()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "laporan.pdf", DocumentFixtures.TextPdf(1));
        var read = await ReadAsync(conversation, attachmentId);
        await using (var db = NewContext())
            await db.Conversations.Where(c => c.Id == conversation)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.TokensUsed, 5000L));
        var policy = Policy(maxTokensPerConversation: 1000);
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read, policy: policy);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task Cancellation_during_the_visual_call_stops_the_job_without_leaving_render_files()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "campuran.pdf", DocumentFixtures.MixedPdf());
        var read = await ReadAsync(conversation, attachmentId);
        using var cts = new CancellationTokenSource();
        var ai = new ScriptedAi(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Handler(ai).HandleAsync(context, cts.Token));

        Assert.Single(ai.Requests);
        // Gambar halaman hanya hidup di memori: satu-satunya file di storage adalah lampiran aslinya.
        Assert.Equal(new[] { ".pdf" },
            Directory.GetFiles(StorageRoot, "*", SearchOption.AllDirectories).Select(Path.GetExtension).Distinct().ToArray());
    }

    [Fact]
    public async Task A_job_without_its_source_message_fails_before_any_ai_call()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "laporan.pdf", DocumentFixtures.TextPdf(1));
        var read = await ReadAsync(conversation, attachmentId);
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(), read);
        var missing = context with { SourceMessageId = context.SourceMessageId + 1000 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(missing, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task A_dependency_attachment_from_another_conversation_is_rejected()
    {
        var mine = await NewConversationAsync();
        var theirs = await NewConversationAsync();
        var foreignAttachment = await UploadAsync(theirs, "laporan.pdf", DocumentFixtures.TextPdf(1));
        var foreignRead = await ReadAsync(theirs, foreignAttachment);
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(mine, JobTypes.DocumentAnalyze, AnalyzeInput(), foreignRead);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task Missing_or_malformed_dependency_output_fails_before_any_ai_call()
    {
        var conversation = await NewConversationAsync();
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));

        var missing = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(missing, CancellationToken.None));

        var malformed = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, AnalyzeInput(),
            dependencyOutput: """{"attachmentId":"x"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(malformed, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }

    [Theory]
    [InlineData("bukan json", true)]
    [InlineData("""{"tujuan":""}""", false)]
    [InlineData("""{"tujuan":"ok","langsung":"ya"}""", false)]
    public async Task Invalid_job_input_fails_before_any_ai_call(string inputJson, bool rawInput)
    {
        var conversation = await NewConversationAsync();
        var ai = new ScriptedAi(_ => Answer(ValidOnPageOne));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentAnalyze, inputJson, rawInput: rawInput);

        await Assert.ThrowsAsync<FormatException>(() => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }
}
