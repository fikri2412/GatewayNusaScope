using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Api.Auth;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Api;

/// <summary>
/// Koleksi jawaban tersimpan per user (§4.4). Isinya snapshot: sesi boleh dihapus tanpa menghapus jawabannya.
/// Dipetakan dari <see cref="ConversationEndpoints"/> supaya rute ikut grup API percakapan tanpa mengubah Program.
/// </summary>
public static class CollectionEndpoints
{
    public const string NotAnAnswerCode = "not_an_answer", EmptyAnswerCode = "empty_answer";
    private const string MessageIdRequiredCode = "message_id_required";

    public sealed record SaveAnswerRequest(long MessageId);

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/collection");
        g.MapGet("", ListAsync);
        g.MapPost("", SaveAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    // Handler publik (seperti helper ConversationEndpoints): status dan isinya diuji tanpa HTTP.
    public static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var rows = await db.SavedAnswers.AsNoTracking().Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).ToListAsync(ct);
        return Results.Ok(rows.Select(s => s.ToDto()));
    }

    /// <summary>
    /// Simpan jawaban assistant yang terlihat dan masih milik user (lewat percakapannya). Simpan ulang pesan yang
    /// sama bukan galat: 200 dengan baris yang sudah ada, bukan duplikat (indeks unik user+message).
    /// </summary>
    public static async Task<IResult> SaveAsync(SaveAnswerRequest? req, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        if (req is not { MessageId: > 0 })
            return Results.Json(new { error = "messageId wajib diisi.", code = MessageIdRequiredCode },
                statusCode: StatusCodes.Status400BadRequest);

        var existing = await db.SavedAnswers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.MessageId == req.MessageId, ct);
        if (existing is not null) return Results.Ok(existing.ToDto());

        // Kepemilikan lewat percakapan pesannya; pesan user lain sama saja tidak ada.
        var row = await (from m in db.Messages.AsNoTracking()
                         join c in db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
                         where m.Id == req.MessageId && m.IsVisible && c.UserId == userId
                         select new { m.ConversationId, m.Role, m.Content, m.ModelAlias, c.Title }).FirstOrDefaultAsync(ct);
        if (row is null) return Results.NotFound();
        if (row.Role != MessageRoles.Assistant)
            return Results.Json(new { error = "Hanya jawaban asisten yang bisa disimpan ke koleksi.", code = NotAnAnswerCode },
                statusCode: StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(row.Content))
            return Results.Json(new { error = "Jawaban kosong tidak bisa disimpan ke koleksi.", code = EmptyAnswerCode },
                statusCode: StatusCodes.Status400BadRequest);

        var saved = new SavedAnswer
        {
            UserId = userId,
            ConversationId = row.ConversationId,
            MessageId = req.MessageId,
            SessionTitle = row.Title,
            ModelAlias = row.ModelAlias,
            Content = row.Content!,
            CreatedAt = DateTime.UtcNow,
        };
        db.SavedAnswers.Add(saved);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Dua permintaan simpan serentak: yang kalah memakai baris pemenang, sama seperti pemeriksaan di atas.
            db.Entry(saved).State = EntityState.Detached;
            existing = await db.SavedAnswers.AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId && s.MessageId == req.MessageId, ct);
            if (existing is null) throw;
            return Results.Ok(existing.ToDto());
        }
        return Results.Json(saved.ToDto(), statusCode: StatusCodes.Status201Created);
    }

    public static async Task<IResult> DeleteAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var removed = await db.SavedAnswers
            .Where(s => s.Id == id && s.UserId == http.User.UserId()).ExecuteDeleteAsync(ct);
        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }
}
