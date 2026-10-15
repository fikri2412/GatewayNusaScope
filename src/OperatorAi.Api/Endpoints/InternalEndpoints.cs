using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OperatorAi.Api.Dispatch;
using OperatorAi.Api.Hubs;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Api;

public static class InternalEndpoints
{
    // Batas batch stream; body JSON jauh lebih besar dari satu batch 100 ms yang wajar.
    private const int MaxStreamBodyBytes = 64 * 1024;
    private const int MaxRunIdLength = 50, MaxStreamTextLength = 32 * 1024, MaxSequence = 1_000_000, MaxOffset = 16_777_216;

    // Dijaga X-Internal-Key, bukan JWT (pemanggilnya worker, bukan user).
    public static void MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/notify", NotifyAsync).AllowAnonymous();
        app.MapPost("/internal/stream", StreamAsync).AllowAnonymous();
    }

    /// <summary>Perbandingan waktu-konstan supaya key tidak bisa ditebak dari selisih waktu respons.</summary>
    public static bool KeyMatches(string? supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied ?? ""), Encoding.UTF8.GetBytes(expected));

    /// <summary>Worker memberi tahu ada pesan/job baru: dorong ke client lewat SignalR dan bangunkan dispatcher.</summary>
    private static async Task<IResult> NotifyAsync(NotifyRequest req, HttpRequest http, IOptions<InternalOptions> options,
        ChatNotifier notifier, WorkerDispatcher dispatcher, CancellationToken ct)
    {
        if (!KeyMatches(http.Headers["X-Internal-Key"].FirstOrDefault(), options.Value.Key)) return Results.Unauthorized();

        var found = req.Type switch
        {
            NotifyTypes.MessageCreated => await notifier.PublishMessageAsync(req.ConversationId, req.Id, ct),
            NotifyTypes.JobUpdated => await notifier.PublishJobAsync(req.ConversationId, req.Id, ct),
            NotifyTypes.ActivityChanged => await notifier.PublishActivityAsync(req.ConversationId, req.Id, ct),
            _ => (bool?)null,
        };
        if (found is null) return Results.BadRequest(new { error = $"type tidak dikenal: {req.Type}" });
        if (found is false) return Results.NotFound();

        dispatcher.Signal();
        return Results.NoContent();
    }

    /// <summary>
    /// Batch draft streaming dari worker: autentikasi key dulu, batasi body, baru parse dan validasi terhadap tiket,
    /// lalu teruskan ke group percakapan. Tidak ada baris database yang ditulis dan dispatcher tidak dibangunkan:
    /// kegagalan di sini hanya kehilangan batch draft, bukan tiket (balasan canonical tetap lewat <c>/internal/notify</c>).
    /// </summary>
    private static async Task<IResult> StreamAsync(HttpRequest http, AppDbContext db, IOptions<InternalOptions> options,
        ChatNotifier notifier, CancellationToken ct)
    {
        if (!KeyMatches(http.Headers["X-Internal-Key"].FirstOrDefault(), options.Value.Key)) return Results.Unauthorized();

        // Batas host bila bisa diubah; bounded read juga berlaku untuk body chunked dan IIS dengan batas host tetap.
        var maxBody = http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBody is { IsReadOnly: false }) maxBody.MaxRequestBodySize = MaxStreamBodyBytes;
        if (http.ContentLength > MaxStreamBodyBytes) return BodyTooLarge();

        StreamEvent? req;
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(MaxStreamBodyBytes + 1);
            try
            {
                var count = 0;
                while (count <= MaxStreamBodyBytes)
                {
                    var read = await http.Body.ReadAsync(buffer.AsMemory(count, MaxStreamBodyBytes + 1 - count), ct);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaxStreamBodyBytes) return BodyTooLarge();
                req = JsonSerializer.Deserialize<StreamEvent>(buffer.AsSpan(0, count), JsonSerializerOptions.Web);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Body bukan batch stream yang valid." });
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return BodyTooLarge();
        }
        if (req is null) return Results.BadRequest(new { error = "Body bukan batch stream yang valid." });

        var (status, error) = await ForwardStreamEventAsync(db, notifier, req, ct);
        return error is null ? Results.NoContent() : Results.Json(new { error }, statusCode: status);
    }

    private static IResult BodyTooLarge() =>
        Results.Json(new { error = $"Body batch stream maksimal {MaxStreamBodyBytes} byte." },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Validasi batch lalu publikasikan: tiket harus milik percakapan yang sama, berstatus Processing, dan masih
    /// dikunci run ini — run lama tidak pernah mendapat kesempatan mengirim ke group percakapan lain.
    /// Hasilnya error (status + pesan) atau status 204 tanpa error; pemanggil HTTP memetakannya ke respons.
    /// </summary>
    public static async Task<(int Status, string? Error)> ForwardStreamEventAsync(AppDbContext db, ChatNotifier notifier,
        StreamEvent req, CancellationToken ct)
    {
        if (Invalid(req) is { } shape) return (StatusCodes.Status400BadRequest, shape);

        // Kunci klaim hingga event terkirim: recovery tidak boleh mengganti run di antara check dan publish.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var owned = await db.QueueItems.FromSqlInterpolated(
            $"SELECT * FROM queue_items WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE id = {req.QueueItemId}")
            .AsNoTracking().AnyAsync(q => q.ConversationId == req.ConversationId
                && q.Status == Statuses.Processing && q.LockedBy == req.RunId, ct);
        if (!owned)
            // 409 (bukan 404): worker membacanya sebagai lease hilang dan berhenti memproduksi draft.
            return (StatusCodes.Status409Conflict, "Batch stream tidak cocok dengan tiket Processing milik run ini.");

        await notifier.StreamEventAsync(req.ConversationId, req, ct);
        await tx.CommitAsync(ct);
        return (StatusCodes.Status204NoContent, null);
    }

    /// <summary>Bentuk batch: kind dikenal, id/run valid, urutan/offset/text sesuai aturan kind.</summary>
    private static string? Invalid(StreamEvent req)
    {
        if (req.QueueItemId <= 0 || req.ConversationId == Guid.Empty || req.StreamId == Guid.Empty)
            return "queueItemId/conversationId/streamId tidak valid.";
        if (req.RunId is not { Length: > 0 and <= MaxRunIdLength })
            return $"runId wajib 1–{MaxRunIdLength} karakter.";
        if (req.Round < 0) return "round tidak valid.";
        if (req.Text is { Length: > MaxStreamTextLength }) return $"text maksimal {MaxStreamTextLength} karakter.";
        if (req.Sequence is < 0 or > MaxSequence || req.Offset is < 0 or > MaxOffset) return "sequence/offset di luar batas.";

        return req.Kind switch
        {
            StreamEventKinds.Started when req.Sequence == 0 && req.Offset == 0 && req.Text is null => null,
            StreamEventKinds.Delta when req.Sequence > 0 && req.Text is { Length: > 0 } => null,
            StreamEventKinds.Aborted when req.Sequence > 0 && req.Text is null => null,
            _ => $"Batch stream tidak valid (kind={req.Kind}).",
        };
    }
}
