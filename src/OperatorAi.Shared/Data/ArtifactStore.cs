using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace OperatorAi.Shared.Data;

/// <summary>Batas artifact; nilai tetap di kode (bukan pengaturan) supaya satu sumber untuk API dan tool AI.</summary>
public static class ArtifactLimits
{
    public const int MaxContentChars = 200_000, MaxTitleChars = 200, MaxVersions = 200, MaxPerConversation = 50,
        MaxLanguageChars = 40;
}

public enum ArtifactStatus { Ok, Created, Unchanged, Conflict, VersionLimit, ArtifactLimit, NotFound }

/// <param name="Artifact">Baris artifact sesudah operasi (null untuk NotFound/ArtifactLimit).</param>
/// <param name="Content">Isi versi aktif sesudah operasi.</param>
public sealed record ArtifactWrite(ArtifactStatus Status, Artifact? Artifact = null, string? Content = null);

/// <summary>
/// Satu-satunya jalur menulis artifact (dipakai endpoint API dan tool AI). Penyimpanan wajib menyebut versi dasarnya;
/// naik versi dilakukan atomik dengan <c>UPDATE ... WHERE current_version = base</c>, jadi dua penyimpan serentak
/// tidak saling menimpa: yang kalah mendapat <see cref="ArtifactStatus.Conflict"/>.
/// </summary>
public static partial class ArtifactStore
{
    [GeneratedRegex("^[a-z0-9+#._-]{1,40}$")]
    private static partial Regex LanguagePattern();

    /// <summary>Pesan galat input (Indonesia) atau null bila valid. Kode mesin selalu <c>invalid_artifact</c>.</summary>
    public static string? Validate(string? kind, string? title, string? language, string? content)
    {
        if (kind is null || !ArtifactKinds.All.Contains(kind))
            return $"kind harus salah satu dari: {string.Join(", ", ArtifactKinds.All)}.";
        if (string.IsNullOrWhiteSpace(title)) return "Judul wajib diisi.";
        if (title.Trim().Length > ArtifactLimits.MaxTitleChars) return $"Judul maksimal {ArtifactLimits.MaxTitleChars} karakter.";
        if (language is not null && !LanguagePattern().IsMatch(language))
            return "bahasa hanya boleh huruf kecil, angka, dan + # . _ - (maksimal 40 karakter).";
        return ValidateContent(content);
    }

    public static string? ValidateContent(string? content)
    {
        if (content is null) return "Isi wajib ada (boleh kosong, tidak boleh null).";
        if (content.Length > ArtifactLimits.MaxContentChars) return $"Isi maksimal {ArtifactLimits.MaxContentChars:N0} karakter.";
        return null;
    }

    /// <summary>Bahasa kanonik: hanya kode yang punya bahasa (tanpa bahasa jatuh ke <c>text</c>); dokumen dan diagram null.</summary>
    public static string? NormalizeLanguage(string kind, string? language) =>
        kind != ArtifactKinds.Code ? null : string.IsNullOrWhiteSpace(language) ? "text" : language.Trim().ToLowerInvariant();

    public static async Task<ArtifactWrite> CreateAsync(AppDbContext db, Guid conversationId, string kind, string title,
        string? language, string content, string source, long? sourceMessageId, CancellationToken ct)
    {
        var ownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await using (ownTransaction)
        {
            if (await db.Artifacts.CountAsync(a => a.ConversationId == conversationId, ct) >= ArtifactLimits.MaxPerConversation)
                return new ArtifactWrite(ArtifactStatus.ArtifactLimit);

            var now = DateTime.UtcNow;
            var artifact = new Artifact
            {
                ConversationId = conversationId, Kind = kind, Title = title.Trim(), Language = NormalizeLanguage(kind, language),
                CurrentVersion = 1, SourceMessageId = sourceMessageId, CreatedAt = now, UpdatedAt = now,
            };
            db.Artifacts.Add(artifact);
            await db.SaveChangesAsync(ct); // Id (NEWSEQUENTIALID) dibutuhkan baris versi
            db.ArtifactVersions.Add(new ArtifactVersion
            {
                ArtifactId = artifact.Id, Version = 1, Content = content, Source = source, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            if (ownTransaction is not null) await ownTransaction.CommitAsync(ct);
            return new ArtifactWrite(ArtifactStatus.Created, artifact, content);
        }
    }

    /// <summary>
    /// Versi baru dari <paramref name="baseVersion"/>. Isi (dan judul bila diganti) sama dengan versi aktif →
    /// <see cref="ArtifactStatus.Unchanged"/> tanpa versi baru. <paramref name="baseVersion"/> bukan versi aktif →
    /// <see cref="ArtifactStatus.Conflict"/>; baris yang dikembalikan membawa versi aktif sebenarnya.
    /// </summary>
    public static async Task<ArtifactWrite> SaveVersionAsync(AppDbContext db, Guid artifactId, int baseVersion, string content,
        string? title, string source, CancellationToken ct)
    {
        var ownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await using (ownTransaction)
        {
            var artifact = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artifactId, ct);
            if (artifact is null) return new ArtifactWrite(ArtifactStatus.NotFound);
            if (artifact.CurrentVersion != baseVersion) return new ArtifactWrite(ArtifactStatus.Conflict, artifact);
            if (artifact.CurrentVersion >= ArtifactLimits.MaxVersions) return new ArtifactWrite(ArtifactStatus.VersionLimit, artifact);

            var newTitle = string.IsNullOrWhiteSpace(title) ? artifact.Title : title.Trim();
            var current = await db.ArtifactVersions.AsNoTracking()
                .Where(v => v.ArtifactId == artifactId && v.Version == artifact.CurrentVersion)
                .Select(v => v.Content).FirstAsync(ct);
            if (current == content && newTitle == artifact.Title) return new ArtifactWrite(ArtifactStatus.Unchanged, artifact, current);

            var next = baseVersion + 1;
            var now = DateTime.UtcNow;
            var updated = await db.Artifacts.Where(a => a.Id == artifactId && a.CurrentVersion == baseVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.CurrentVersion, next)
                    .SetProperty(a => a.Title, newTitle)
                    .SetProperty(a => a.UpdatedAt, now), ct);
            if (updated == 0)
            {
                // Penyimpan lain menang di antara pembacaan dan UPDATE.
                var latest = await db.Artifacts.AsNoTracking().FirstAsync(a => a.Id == artifactId, ct);
                return new ArtifactWrite(ArtifactStatus.Conflict, latest);
            }

            db.ArtifactVersions.Add(new ArtifactVersion
            {
                ArtifactId = artifactId, Version = next, Content = content, Source = source, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            if (ownTransaction is not null) await ownTransaction.CommitAsync(ct);
            artifact = await db.Artifacts.AsNoTracking().FirstAsync(a => a.Id == artifactId, ct);
            return new ArtifactWrite(ArtifactStatus.Ok, artifact, content);
        }
    }
}
