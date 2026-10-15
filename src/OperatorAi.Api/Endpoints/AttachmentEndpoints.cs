using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Api.Auth;
using OperatorAi.Api.Contracts;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;

namespace OperatorAi.Api;

public static class AttachmentEndpoints
{
    private const int MaxFileNameLength = 260, MultipartOverheadBytes = 64 * 1024;

    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Antiforgery dimatikan: API ini dipanggil SPA lintas origin dengan header Authorization (bukan form HTML bercookie).
        app.MapPost("/api/conversations/{id:guid}/attachments", UploadEndpoint).DisableAntiforgery();
        app.MapGet("/api/attachments/{id:guid}/download", DownloadAsync);
        app.MapGet("/api/attachment-policy", AttachmentPolicy);
    }

    /// <summary>
    /// Kebijakan lampiran efektif untuk UI; dibaca dari satu <see cref="GatewaySettingsProvider.CurrentPolicy"/> sehingga
    /// angka yang ditampilkan sama dengan yang ditegakkan server, bukan default yang di-hardcode klien.
    /// </summary>
    public static IResult AttachmentPolicy(GatewaySettingsProvider settings)
    {
        var policy = settings.CurrentPolicy;
        return Results.Ok(new AttachmentPolicyDto(policy.Upload.MaxBytes, policy.Upload.AllowedExtensions,
            policy.Upload.MaxAttachmentsPerMessage, policy.Document.MaxImagePixels,
            policy.SourceSettingsVersion, policy.PolicyHash));
    }

    /// <summary>Endpoint: satu snapshot kebijakan per unggahan; handler memakai snapshot itu sampai selesai, bukan setting terbaru.</summary>
    private static Task<IResult> UploadEndpoint(Guid id, HttpContext http, AppDbContext db, FileStorage storage,
        GatewaySettingsProvider settings, CancellationToken ct) =>
        UploadAsync(id, http, db, storage, settings.CurrentPolicy, ct);

    /// <summary>
    /// Handler unggahan. Publik (seperti helper <see cref="ConversationEndpoints"/>) supaya batas multipart bisa
    /// diuji tanpa HTTP: jalur chunked/IIS tidak punya Content-Length, jadi batasnya ditegakkan saat membaca body.
    /// </summary>
    public static async Task<IResult> UploadAsync(Guid id, HttpContext http, AppDbContext db, FileStorage storage,
        ExecutionPolicy policy, CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == http.User.UserId(), ct))
            return Results.NotFound();
        var limits = policy.Upload;
        var bodyLimit = limits.MaxBytes + MultipartOverheadBytes;
        if (http.Request.ContentLength > bodyLimit) return TooLarge(limits);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = bodyLimit;
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Upload harus multipart/form-data dengan satu field file." });
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128)
            return Results.BadRequest(new { error = "Boundary multipart tidak valid." });

        string? savedPath = null;
        var committed = false;
        try
        {
            // Baca langsung ke folder lokal, bukan IFormFile yang membuffer seluruh body sebelum validasi.
            using var body = new UploadBodyStream(http.Request.Body, bodyLimit);
            // Batas per-section memakai batas body yang sama, bukan MaxBytes: batas yang lebih kecil menolak file
            // di atas MaxBytes di jalur chunked sebagai "multipart tidak valid" (400), padahal jalur Content-Length menjawab 413.
            var reader = new MultipartReader(boundary, body) { BodyLengthLimit = bodyLimit };
            var section = await ReadSectionAsync(reader, ct);
            if (section is null || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || disposition.DispositionType != "form-data" || HeaderUtilities.RemoveQuotes(disposition.Name).Value != "file")
                return Results.BadRequest(new { error = "file wajib diisi." });
            var fileName = Path.GetFileName((HeaderUtilities.RemoveQuotes(
                disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "").Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName)) return Results.BadRequest(new { error = "Nama file wajib diisi." });
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!limits.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Format tidak didukung; video tidak didukung. Yang diizinkan: {string.Join(", ", limits.AllowedExtensions)}." });

            var attachmentId = Guid.NewGuid();
            savedPath = FileStorage.UploadPath(id, attachmentId, extension);
            await storage.SaveAsync(savedPath, new SectionBodyStream(section.Body), ct);
            var size = new FileInfo(storage.Resolve(savedPath)).Length;
            if (size == 0) return Results.BadRequest(new { error = "file wajib diisi." });
            if (size > limits.MaxBytes) return TooLarge(limits);
            if (await ReadSectionAsync(reader, ct) is not null)
                return Results.BadRequest(new { error = "Upload hanya menerima satu field file." });
            var contentType = extension switch
            {
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".csv" => "text/csv",
                _ => await InputFileValidation.ValidateAsync(storage.Resolve(savedPath), extension, policy.Document, ct),
            };
            var attachment = new Attachment
            {
                Id = attachmentId, ConversationId = id,
                FileName = fileName.Length > MaxFileNameLength
                    ? fileName[..(MaxFileNameLength - extension.Length)] + extension : fileName,
                StoragePath = savedPath, Kind = AttachmentKinds.Upload, ContentType = contentType, SizeBytes = size,
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(ct);
            committed = true;
            return Results.Created($"/api/attachments/{attachment.Id}/download", attachment.ToDto());
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(limits);
        }
        catch (InvalidDataException)
        {
            return InvalidMultipart(limits);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status400BadRequest)
        {
            return InvalidMultipart(limits);
        }
        catch (FormatException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        finally
        {
            if (!committed && savedPath is not null) File.Delete(storage.Resolve(savedPath));
        }
    }

    /// <summary>Ukuran tanpa menyesatkan saat batasnya bukan kelipatan MB (mis. 64 KB dari kebijakan gateway).</summary>
    internal static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} byte";

    private static IResult TooLarge(UploadRuntimeLimits limits) =>
        Results.Json(new { error = $"Ukuran file maksimal {FormatBytes(limits.MaxBytes)}." }, statusCode: 413);

    private static IResult InvalidMultipart(UploadRuntimeLimits limits) =>
        Results.BadRequest(new { error = $"File atau multipart tidak valid; ukuran maksimal {FormatBytes(limits.MaxBytes)}." });

    /// <summary>
    /// Section berikutnya dari badan multipart. Badan yang habis sebelum delimiter akhir dilaporkan
    /// <see cref="MultipartReader"/> sebagai <see cref="IOException"/> biasa, sedangkan handler hanya menerima 400.
    /// Diterjemahkan di sini, bukan lewat filter IOException yang lebar, supaya kegagalan tulis disk tetap 500.
    /// </summary>
    private static async Task<MultipartSection?> ReadSectionAsync(MultipartReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadNextSectionAsync(ct);
        }
        catch (IOException ex) when (IsTruncatedBody(ex))
        {
            throw Truncated(ex);
        }
    }

    /// <summary>
    /// <see cref="BadHttpRequestException"/> (mis. 413 dari batas body) juga turunan <see cref="IOException"/>
    /// dan tidak boleh ikut jadi 400. InvalidDataException ditangani langsung oleh handler upload.
    /// </summary>
    private static bool IsTruncatedBody(IOException ex) =>
        ex is not BadHttpRequestException;

    private static BadHttpRequestException Truncated(Exception inner) =>
        new("Badan multipart terputus sebelum batas akhir.", StatusCodes.Status400BadRequest, inner);

    /// <summary>
    /// Badan satu section multipart. Hanya pembacaan dari parser yang lewat kelas ini; penulisan ke file di
    /// <c>FileStorage.SaveAsync</c> terjadi di luarnya, jadi kegagalan disk tidak pernah jadi 400.
    /// </summary>
    private sealed class SectionBodyStream(Stream section) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return section.Read(buffer, offset, count);
            }
            catch (IOException ex) when (IsTruncatedBody(ex))
            {
                throw Truncated(ex);
            }
        }

        // Read(Span<byte>) tidak dioverride: implementasi dasar Stream mengarah ke Read(byte[], int, int) di atas,
        // jadi terjemahannya tetap berlaku tanpa cabang tambahan.
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                return await section.ReadAsync(buffer, ct);
            }
            catch (IOException ex) when (IsTruncatedBody(ex))
            {
                throw Truncated(ex);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Batas total tetap berlaku untuk chunked/IIS, tanpa bergantung pada Content-Length atau limit host.</summary>
    private sealed class UploadBodyStream(Stream inner, long limit) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Count(int count)
        {
            read += count;
            if (read > limit) throw new BadHttpRequestException("Upload terlalu besar.", 413);
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<IResult> DownloadAsync(Guid id, HttpContext http, AppDbContext db, FileStorage storage, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        // 404 (bukan 403) untuk lampiran percakapan orang lain: keberadaannya tidak boleh bocor.
        if (attachment is null || !await db.Conversations.AnyAsync(c => c.Id == attachment.ConversationId && c.UserId == userId, ct))
            return Results.NotFound();
        if (!File.Exists(storage.Resolve(attachment.StoragePath))) return Results.NotFound();
        return Results.File(storage.OpenRead(attachment.StoragePath), attachment.ContentType, attachment.FileName);
    }
}
