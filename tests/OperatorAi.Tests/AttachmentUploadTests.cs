using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Api;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

/// <summary>
/// Batas unggahan lampiran lewat handler API nyata tanpa HTTP. Jalur chunked (tanpa Content-Length, seperti IIS)
/// dan jalur Content-Length harus menjawab 413 untuk file di atas batas kebijakan yang berlaku, sedangkan body
/// multipart yang terputus tidak boleh menyisakan baris lampiran maupun file parsial.
/// </summary>
public class AttachmentUploadTests : DocumentTestBase
{
    private const string Boundary = "lab0123456789abcdef0123456789abcdef";

    /// <summary>MaxBytes kecil supaya batasnya cepat diuji; aritmetika batas tidak bergantung skala byte.</summary>
    private static readonly UploadRuntimeLimits Limits = new() { MaxBytes = 64 * 1024 };
    private static readonly ExecutionPolicy UploadPolicy = Policy(upload: Limits);

    private static byte[] Pdf(long size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Multipart(byte[] content, bool closed, string fileName = "laporan.pdf")
    {
        using var ms = new MemoryStream();
        void Write(string text) => ms.Write(Encoding.ASCII.GetBytes(text));
        Write($"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\n");
        Write("Content-Type: application/octet-stream\r\n\r\n");
        ms.Write(content);
        Write("\r\n");
        if (closed) Write($"--{Boundary}--\r\n");
        return ms.ToArray();
    }

    private static DefaultHttpContext Request(byte[] body, long? contentLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = $"multipart/form-data; boundary={Boundary}";
        context.Request.ContentLength = contentLength;
        context.Request.Body = new MemoryStream(body);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test")], "test"));
        return context;
    }

    private async Task<IResult> UploadAsync(Guid conversationId, DefaultHttpContext http, ExecutionPolicy? policy = null)
    {
        await using var db = NewContext();
        return await AttachmentEndpoints.UploadAsync(conversationId, http, db, Storage, policy ?? UploadPolicy, CancellationToken.None);
    }

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    /// <summary>Isi JSON hasil <c>Results.Json(new { error })</c> apa adanya; pesan ditulis untuk user, bukan ditebak.</summary>
    private static string ErrorOf(IResult result) =>
        ((IValueHttpResult)result).Value is { } value ? JsonSerializer.Serialize(value) : "";

    private async Task<long> AttachmentRowsAsync(Guid conversationId)
    {
        await using var db = NewContext();
        return await db.Attachments.CountAsync(a => a.ConversationId == conversationId);
    }

    private int StoredFiles(Guid conversationId)
    {
        var dir = Path.Combine(StorageRoot, "uploads", conversationId.ToString());
        return Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;
    }

    [Fact]
    public async Task Oversized_upload_without_content_length_is_rejected_with_413_and_stores_nothing()
    {
        var conversation = await NewConversationAsync();
        var body = Multipart(Pdf(Limits.MaxBytes + 1), closed: true);

        var result = await UploadAsync(conversation, Request(body, contentLength: null));

        Assert.Equal(413, StatusOf(result));
        Assert.Equal(0, await AttachmentRowsAsync(conversation));
        Assert.Equal(0, StoredFiles(conversation));
    }

    [Fact]
    public async Task Upload_of_exactly_max_bytes_is_accepted()
    {
        var conversation = await NewConversationAsync();
        var body = Multipart(Pdf(Limits.MaxBytes), closed: true);

        var result = await UploadAsync(conversation, Request(body, contentLength: null));

        Assert.Equal(201, StatusOf(result));
        Assert.Equal(1, await AttachmentRowsAsync(conversation));
        Assert.Equal(1, StoredFiles(conversation));
        Assert.Equal(Limits.MaxBytes, new FileInfo(Directory.GetFiles(
            Path.Combine(StorageRoot, "uploads", conversation.ToString()))[0]).Length);
    }

    [Fact]
    public async Task Upload_over_the_total_body_cap_keeps_413_instead_of_multipart_400()
    {
        var conversation = await NewConversationAsync();
        // 70 KiB di atas MaxBytes berarti body melewati batas total (MaxBytes + 64 KiB overhead) saat dibaca,
        // jadi 413 harus datang dari batas byte, bukan diterjemahkan jadi 400 "multipart terputus".
        var body = Multipart(Pdf(Limits.MaxBytes + 70 * 1024), closed: true);

        var result = await UploadAsync(conversation, Request(body, contentLength: null));

        Assert.Equal(413, StatusOf(result));
        Assert.Equal(0, await AttachmentRowsAsync(conversation));
        Assert.Equal(0, StoredFiles(conversation));
    }

    [Fact]
    public async Task Multipart_body_that_ends_before_the_closing_delimiter_leaves_nothing()
    {
        var conversation = await NewConversationAsync();
        var complete = Multipart(Pdf(Limits.MaxBytes), closed: true);
        var truncated = complete[..(complete.Length - (int)(Limits.MaxBytes / 2))];

        var result = await UploadAsync(conversation, Request(truncated, contentLength: truncated.Length));

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(0, await AttachmentRowsAsync(conversation));
        Assert.Equal(0, StoredFiles(conversation));
    }

    /// <summary>
    /// Snapshot gateway yang berlaku, bukan default lokal: ukuran dan allowlist unggahan ditegakkan selama
    /// satu operasi tanpa menyisakan file atau baris pada penolakan.
    /// </summary>
    [Fact]
    public async Task Upload_follows_the_effective_snapshot_without_leaving_rejected_files_or_rows()
    {
        var provider = GatewaySettingsProviderTests.Provider(GatewaySettingsProviderTests.LocalConfiguration());
        using var http = GatewaySettingsProviderTests.Client(new GatewaySettingsProviderTests.Handler(_ =>
            GatewaySettingsProviderTests.Json(
                """[{"key":"Upload:MaxBytes","value":"2048","valueType":"int"},{"key":"Upload:AllowedExtensions","value":".pdf","valueType":"text"}]""")));
        Assert.True(await provider.RefreshAsync(http, CancellationToken.None));

        var conversation = await NewConversationAsync();
        // 3 KiB di atas batas efektif 2 KiB: 413 tanpa baris maupun file tersisa.
        var oversized = await UploadAsync(conversation,
            Request(Multipart(Pdf(3 * 1024), closed: true), contentLength: null), provider.CurrentPolicy);
        Assert.Equal(413, StatusOf(oversized));

        // Allowlist efektif hanya .pdf: .png yang masih diizinkan default lokal ditolak sebelum disimpan.
        var unsupported = await UploadAsync(conversation,
            Request(Multipart([1, 2, 3], closed: true, fileName: "grafik.png"), contentLength: null), provider.CurrentPolicy);
        Assert.Equal(400, StatusOf(unsupported));
        Assert.Equal(0, StoredFiles(conversation));

        // PDF 1 KiB tetap diterima: penolakan mengikuti kebijakan, bukan tebakan.
        var accepted = await UploadAsync(conversation,
            Request(Multipart(Pdf(1024), closed: true), contentLength: null), provider.CurrentPolicy);
        Assert.Equal(201, StatusOf(accepted));
        Assert.Equal(1, await AttachmentRowsAsync(conversation));
        Assert.Equal(1, StoredFiles(conversation));
    }
}
