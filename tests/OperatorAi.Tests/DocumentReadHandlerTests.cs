using System.Text.Json;
using SkiaSharp;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

/// <summary>
/// Job DocumentRead (6C): teks per halaman dipetakan sesuai nomor halaman PDF, halaman yang hanya bisa dibaca
/// dari gambar (scan/grafik/ file gambar) ditandai untuk vision atau ditolak jujur, dan lampiran hanya boleh
/// berasal dari percakapan job itu sendiri.
/// </summary>
public class DocumentReadHandlerTests : DocumentTestBase
{
    private DocumentReadHandler Handler() => new(DbOptions, Storage);

    private static async Task<JsonElement> ReadAsync(DocumentReadHandler handler, JobContext context)
    {
        var output = await handler.HandleAsync(context, CancellationToken.None);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    [Fact]
    public async Task Text_pdf_pages_are_mapped_by_their_page_number()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "laporan.pdf", DocumentFixtures.TextPdf(2));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId));

        var output = await ReadAsync(Handler(), context);

        Assert.Equal(attachmentId.ToString(), output.GetProperty("attachmentId").GetString());
        Assert.Equal("laporan.pdf", output.GetProperty("fileName").GetString());
        Assert.Equal("pdf", output.GetProperty("kind").GetString());
        Assert.Equal(2, output.GetProperty("pageCount").GetInt32());
        var pages = output.GetProperty("pages").EnumerateArray().ToArray();
        Assert.Equal(1, pages[0].GetProperty("pageNumber").GetInt32());
        Assert.Contains("HALAMAN-1", pages[0].GetProperty("text").GetString());
        Assert.Equal(2, pages[1].GetProperty("pageNumber").GetInt32());
        Assert.Contains("HALAMAN-2", pages[1].GetProperty("text").GetString());
        Assert.True(output.GetProperty("extractedChars").GetInt32() > 0);
        Assert.Empty(output.GetProperty("visualPages").EnumerateArray());
        Assert.Empty(output.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Scanned_page_is_marked_for_vision_and_requires_a_vision_model()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "scan.pdf", DocumentFixtures.ScannedPdf());
        var handler = Handler();

        var supported = await ReadAsync(handler,
            await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId)));
        Assert.Equal(1, supported.GetProperty("pageCount").GetInt32());
        Assert.Equal("", supported.GetProperty("pages")[0].GetProperty("text").GetString());
        Assert.Equal(new[] { 1 }, supported.GetProperty("visualPages").EnumerateArray().Select(p => p.GetInt32()).ToArray());
        Assert.Contains("Halaman 1 tidak memuat teks", supported.GetProperty("warnings")[0].GetString());

        var unsupported = await NewJobContextAsync(conversation, JobTypes.DocumentRead,
            ReadInput(attachmentId, supportsVision: false));
        var error = await Assert.ThrowsAsync<DocumentInputException>(() => handler.HandleAsync(unsupported, CancellationToken.None));
        Assert.Contains("tidak mendukung vision", error.Message);
    }

    [Fact]
    public async Task Mixed_pdf_marks_only_the_image_page_as_visual()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "campuran.pdf", DocumentFixtures.MixedPdf());
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId));

        var output = await ReadAsync(Handler(), context);

        Assert.Equal(2, output.GetProperty("pageCount").GetInt32());
        Assert.Equal(new[] { 2 }, output.GetProperty("visualPages").EnumerateArray().Select(p => p.GetInt32()).ToArray());
        Assert.Contains("gambar/grafik", output.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task A_page_with_short_text_is_still_a_text_page_not_a_visual_one()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "pendek.pdf", DocumentFixtures.ShortTextPdf());
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId, supportsVision: false));

        var output = await ReadAsync(Handler(), context);

        // Halaman judul/pembatas berisi teks pendek tidak boleh dipaksa butuh model vision.
        Assert.Contains("Judul", output.GetProperty("pages")[0].GetProperty("text").GetString());
        Assert.Empty(output.GetProperty("visualPages").EnumerateArray());
        Assert.Empty(output.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Visual_true_marks_every_page_of_a_text_pdf()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "teks.pdf", DocumentFixtures.TextPdf(3));
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId, visual: true));

        var output = await ReadAsync(Handler(), context);

        Assert.Equal(new[] { 1, 2, 3 }, output.GetProperty("visualPages").EnumerateArray().Select(p => p.GetInt32()).ToArray());
        Assert.Contains("semua 3 halaman", output.GetProperty("warnings")[0].GetString());
    }

    [Theory]
    [InlineData("gambar.png")]
    [InlineData("gambar.jpg")]
    public async Task Image_upload_is_a_single_page_that_requires_vision(string fileName)
    {
        var conversation = await NewConversationAsync();
        var bytes = fileName.EndsWith(".png") ? DocumentFixtures.HalvesPng() : DocumentFixtures.HalvesJpeg();
        var attachmentId = await UploadAsync(conversation, fileName, bytes);
        var handler = Handler();

        var output = await ReadAsync(handler, await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId)));
        Assert.Equal("image", output.GetProperty("kind").GetString());
        Assert.Equal(1, output.GetProperty("pageCount").GetInt32());
        Assert.Equal("", output.GetProperty("pages")[0].GetProperty("text").GetString());
        Assert.Equal(new[] { 1 }, output.GetProperty("visualPages").EnumerateArray().Select(p => p.GetInt32()).ToArray());

        var unsupported = await NewJobContextAsync(conversation, JobTypes.DocumentRead,
            ReadInput(attachmentId, supportsVision: false));
        await Assert.ThrowsAsync<DocumentInputException>(() => handler.HandleAsync(unsupported, CancellationToken.None));
    }

    [Fact]
    public async Task Encrypted_pdf_passes_upload_signature_validation_but_is_rejected_by_the_job()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "terkunci.pdf", DocumentFixtures.EncryptedPdf());
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId));

        // API hanya bisa memeriksa tanda tangan; parser penuh yang mendeteksi password berjalan di job terisolasi.
        Assert.Equal("application/pdf",
            await InputFileValidation.ValidateAsync(
                Storage.Resolve(RelativePath(conversation, attachmentId, "terkunci.pdf")), ".pdf",
                DocumentRuntimeLimits.Default, CancellationToken.None));

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(context, CancellationToken.None));
        Assert.Contains("terkunci password", error.Message);
    }

    [Fact]
    public async Task Corrupt_pdf_is_rejected_as_a_document_error()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "rusak.pdf", DocumentFixtures.CorruptPdf());
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId));

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(context, CancellationToken.None));
        Assert.Contains("tidak dapat dibaca", error.Message);
    }

    [Fact]
    public async Task Pdf_over_the_configured_page_limit_is_rejected_instead_of_trimmed()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "besar.pdf", DocumentFixtures.TextPdf(3));
        var policy = Policy(document: DocumentRuntimeLimits.Default with { MaxPages = 2 });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId), policy: policy);

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(context, CancellationToken.None));
        Assert.Contains("maksimal 2 halaman", error.Message);
    }

    [Fact]
    public async Task Pdf_text_over_the_configured_character_limit_is_rejected_on_the_offending_page()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "panjang.pdf", DocumentFixtures.TextPdf(2));
        var policy = Policy(document: DocumentRuntimeLimits.Default with { MaxExtractedChars = 5 });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId), policy: policy);

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(context, CancellationToken.None));
        Assert.Contains("melebihi batas 5 karakter (terlampaui pada halaman 1)", error.Message);
    }

    [Fact]
    public async Task Image_over_the_configured_pixel_limit_is_rejected()
    {
        var conversation = await NewConversationAsync();
        using var bitmap = new SKBitmap(new SKImageInfo(5000, 4000));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var attachmentId = await UploadAsync(conversation, "besar.png", data.ToArray());
        var policy = Policy(document: DocumentRuntimeLimits.Default with { MaxImagePixels = 4_000_000 });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId), policy: policy);

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(context, CancellationToken.None));
        Assert.Contains("4000000 piksel", error.Message);
    }

    [Fact]
    public async Task Format_outside_the_job_snapshot_extensions_is_rejected()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "laporan.pdf", DocumentFixtures.TextPdf(1));
        var policy = Policy(upload: UploadRuntimeLimits.Default with { AllowedExtensions = [".png"] });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId), policy: policy);

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(context, CancellationToken.None));
        Assert.Contains("hanya .png", error.Message);
    }

    [Fact]
    public async Task Attachment_bytes_over_the_job_snapshot_limit_are_rejected()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "gambar.png", DocumentFixtures.HalvesPng());
        var policy = Policy(upload: UploadRuntimeLimits.Default with { MaxBytes = 32 });
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId), policy: policy);

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(context, CancellationToken.None));
        Assert.Contains("melebihi batas 32 byte", error.Message);
    }

    [Fact]
    public async Task Rejects_an_attachment_from_another_conversation()
    {
        var mine = await NewConversationAsync();
        var theirs = await NewConversationAsync();
        var foreign = await UploadAsync(theirs, "laporan.pdf", DocumentFixtures.TextPdf(1));
        var context = await NewJobContextAsync(mine, JobTypes.DocumentRead, ReadInput(foreign));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_an_output_attachment_and_an_unsupported_extension()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await UploadAsync(conversation, "laporan.pdf", DocumentFixtures.TextPdf(1));
        await using (var db = NewContext())
        {
            var attachment = await db.Attachments.FindAsync(attachmentId);
            attachment!.Kind = AttachmentKinds.Output;
            await db.SaveChangesAsync();
        }
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(attachmentId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(context, CancellationToken.None));

        var textId = await UploadAsync(conversation, "catatan.txt", "teks"u8.ToArray());
        var textContext = await NewJobContextAsync(conversation, JobTypes.DocumentRead, ReadInput(textId));
        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler().HandleAsync(textContext, CancellationToken.None));
        Assert.Contains("tidak didukung", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"attachmentId":"bukan-guid"}""")]
    [InlineData("""{"attachmentId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task Rejects_invalid_input_before_touching_the_attachment(string inputJson)
    {
        var conversation = await NewConversationAsync();
        var context = await NewJobContextAsync(conversation, JobTypes.DocumentRead, inputJson);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(context, CancellationToken.None));
    }
}
