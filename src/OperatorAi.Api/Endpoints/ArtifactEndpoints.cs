using Microsoft.EntityFrameworkCore;
using OperatorAi.Api.Auth;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;

namespace OperatorAi.Api;

/// <summary>
/// Artifact Canvas (dokumen/kode) milik percakapan user. Semua rute memeriksa kepemilikan lewat percakapan artifact;
/// artifact orang lain sama saja tidak ada (404). Penulisan lewat <see cref="ArtifactStore"/>, yang juga dipakai tool AI.
/// Dipetakan dari <see cref="ConversationEndpoints"/> supaya Program tidak perlu tahu grup endpoint ini.
/// </summary>
public static class ArtifactEndpoints
{
    public const string InvalidCode = "invalid_artifact", ConflictCode = "version_conflict",
        VersionLimitCode = "version_limit", ArtifactLimitCode = "artifact_limit";

    public sealed record CreateArtifactRequest(string? Kind, string? Title, string? Language, string? Content);
    public sealed record SaveArtifactRequest(int BaseVersion, string? Content, string? Title);
    public sealed record RestoreArtifactRequest(int Version, int BaseVersion);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/conversations/{id:guid}/artifacts", ListAsync);
        app.MapPost("/api/conversations/{id:guid}/artifacts", CreateAsync);
        var g = app.MapGroup("/api/artifacts");
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPut("/{id:guid}", SaveAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapGet("/{id:guid}/versions", VersionsAsync);
        g.MapGet("/{id:guid}/versions/{version:int}", VersionAsync);
        g.MapPost("/{id:guid}/restore", RestoreAsync);
    }

    // Handler publik (seperti CollectionEndpoints): status dan isinya diuji tanpa HTTP.
    public static async Task<IResult> ListAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!await OwnsConversation(db, id, http.User.UserId(), ct)) return Results.NotFound();
        var rows = await db.Artifacts.AsNoTracking().Where(a => a.ConversationId == id)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct);
        return Results.Ok(rows.Select(a => a.ToSummary()));
    }

    public static async Task<IResult> CreateAsync(Guid id, CreateArtifactRequest? req, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        if (!await OwnsConversation(db, id, http.User.UserId(), ct)) return Results.NotFound();
        var invalid = ArtifactStore.Validate(req?.Kind, req?.Title, req?.Language, req?.Content);
        if (invalid is not null) return Error(StatusCodes.Status400BadRequest, invalid, InvalidCode);

        var result = await ArtifactStore.CreateAsync(db, id, req!.Kind!, req.Title!, req.Language, req.Content!,
            ArtifactSources.User, sourceMessageId: null, ct);
        return result.Status == ArtifactStatus.ArtifactLimit
            ? Error(StatusCodes.Status409Conflict, $"Satu sesi maksimal {ArtifactLimits.MaxPerConversation} artifact.", ArtifactLimitCode)
            : Results.Json(result.Artifact!.ToDto(result.Content!), statusCode: StatusCodes.Status201Created);
    }

    public static async Task<IResult> GetAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var artifact = await OwnedAsync(db, id, http.User.UserId(), ct);
        if (artifact is null) return Results.NotFound();
        var content = await db.ArtifactVersions.AsNoTracking()
            .Where(v => v.ArtifactId == id && v.Version == artifact.CurrentVersion)
            .Select(v => v.Content).FirstAsync(ct);
        return Results.Ok(artifact.ToDto(content));
    }

    /// <summary>
    /// Simpan versi baru. <c>BaseVersion</c> wajib versi aktif yang dilihat klien; beda → 409 <c>version_conflict</c>
    /// dengan <c>currentVersion</c> sebenarnya, tanpa menimpa apa pun. Isi sama dengan versi aktif → 200 tanpa versi baru.
    /// </summary>
    public static async Task<IResult> SaveAsync(Guid id, SaveArtifactRequest? req, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        if (await OwnedAsync(db, id, http.User.UserId(), ct) is null) return Results.NotFound();
        if (req is not { BaseVersion: > 0 })
            return Error(StatusCodes.Status400BadRequest, "baseVersion wajib diisi.", InvalidCode);
        var invalid = ArtifactStore.ValidateContent(req.Content);
        if (invalid is null && req.Title is { } t && t.Trim().Length > ArtifactLimits.MaxTitleChars)
            invalid = $"Judul maksimal {ArtifactLimits.MaxTitleChars} karakter.";
        if (invalid is not null) return Error(StatusCodes.Status400BadRequest, invalid, InvalidCode);

        return ToResult(await ArtifactStore.SaveVersionAsync(db, id, req.BaseVersion, req.Content!, req.Title,
            ArtifactSources.User, ct));
    }

    public static async Task<IResult> DeleteAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await OwnedAsync(db, id, http.User.UserId(), ct) is null) return Results.NotFound();
        await db.Artifacts.Where(a => a.Id == id).ExecuteDeleteAsync(ct); // versi ikut terhapus (FK cascade)
        return Results.NoContent();
    }

    public static async Task<IResult> VersionsAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await OwnedAsync(db, id, http.User.UserId(), ct) is null) return Results.NotFound();
        var rows = await db.ArtifactVersions.AsNoTracking().Where(v => v.ArtifactId == id)
            .OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.Source, v.CreatedAt, Length = v.Content.Length }).ToListAsync(ct);
        return Results.Ok(rows.Select(v => new ArtifactVersionDto(v.Version, v.Source,
            DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc), v.Length)));
    }

    public static async Task<IResult> VersionAsync(Guid id, int version, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await OwnedAsync(db, id, http.User.UserId(), ct) is null) return Results.NotFound();
        var row = await db.ArtifactVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ArtifactId == id && v.Version == version, ct);
        return row is null
            ? Results.NotFound()
            : Results.Ok(new ArtifactVersionContentDto(row.Version, row.Source,
                DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc), row.Content));
    }

    /// <summary>
    /// Urungkan/pulihkan: isi versi lama disalin sebagai versi BARU (riwayat tidak pernah dihapus atau ditulis ulang).
    /// Tunduk pada aturan konflik yang sama dengan simpan biasa.
    /// </summary>
    public static async Task<IResult> RestoreAsync(Guid id, RestoreArtifactRequest? req, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        if (await OwnedAsync(db, id, http.User.UserId(), ct) is null) return Results.NotFound();
        if (req is not { Version: > 0, BaseVersion: > 0 })
            return Error(StatusCodes.Status400BadRequest, "version dan baseVersion wajib diisi.", InvalidCode);
        var old = await db.ArtifactVersions.AsNoTracking()
            .Where(v => v.ArtifactId == id && v.Version == req.Version).Select(v => v.Content).FirstOrDefaultAsync(ct);
        if (old is null) return Results.NotFound();

        return ToResult(await ArtifactStore.SaveVersionAsync(db, id, req.BaseVersion, old, title: null,
            ArtifactSources.Restore, ct));
    }

    private static IResult ToResult(ArtifactWrite result) => result.Status switch
    {
        ArtifactStatus.Ok or ArtifactStatus.Unchanged => Results.Ok(result.Artifact!.ToDto(result.Content!)),
        ArtifactStatus.NotFound => Results.NotFound(),
        ArtifactStatus.Conflict => Results.Json(new
        {
            error = "Artifact sudah berubah di tempat lain. Muat ulang sebelum menyimpan.",
            code = ConflictCode,
            currentVersion = result.Artifact!.CurrentVersion,
        }, statusCode: StatusCodes.Status409Conflict),
        ArtifactStatus.VersionLimit => Error(StatusCodes.Status409Conflict,
            $"Artifact sudah mencapai {ArtifactLimits.MaxVersions} versi.", VersionLimitCode),
        _ => Error(StatusCodes.Status500InternalServerError, "Hasil penyimpanan artifact tidak dikenal.", "artifact_unknown"),
    };

    private static IResult Error(int status, string error, string code) =>
        Results.Json(new { error, code }, statusCode: status);

    private static Task<bool> OwnsConversation(AppDbContext db, Guid conversationId, string userId, CancellationToken ct) =>
        db.Conversations.AnyAsync(c => c.Id == conversationId && c.UserId == userId, ct);

    /// <summary>Artifact milik user lewat percakapannya; null bila tidak ada atau milik orang lain.</summary>
    private static Task<Artifact?> OwnedAsync(AppDbContext db, Guid artifactId, string userId, CancellationToken ct) =>
        (from a in db.Artifacts.AsNoTracking()
         join c in db.Conversations.AsNoTracking() on a.ConversationId equals c.Id
         where a.Id == artifactId && c.UserId == userId
         select a).FirstOrDefaultAsync(ct);
}
