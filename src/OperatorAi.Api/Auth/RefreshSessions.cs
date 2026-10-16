using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;

namespace OperatorAi.Api.Auth;

/// <summary>Token mentah (cookie refresh, tautan atur ulang) dan hash-nya. DB hanya menyimpan hash, cookie berisi token mentah.</summary>
public static class AuthTokens
{
    /// <summary>32 byte acak base64url: aman dipakai di cookie maupun query string.</summary>
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 hex (64 karakter), sesuai kolom <c>token_hash</c>.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// Sesi refresh: rotasi sekali pakai. Token yang sudah dirotasi dan datang lagi dalam tenggang diabaikan (tab/jaringan
/// ganda); datang lagi setelah tenggang dianggap token bocor, jadi seluruh sesi user dicabut.
/// </summary>
public sealed class RefreshSessions(AppDbContext db, TimeProvider time, IOptions<AuthOptions> options)
{
    /// <summary>Tenggang tab kedua yang kalah balapan: ditolak, tapi tidak mencabut apa pun.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    public async Task<string> IssueAsync(User user, CancellationToken ct)
    {
        var token = AuthTokens.New();
        db.RefreshTokens.Add(Row(user.Id, token, time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>Rotasi. <c>User</c> null berarti token tidak berlaku dan pemanggil menjawab 401.</summary>
    public async Task<(User? User, string? Token)> RotateAsync(string? presented, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (string.IsNullOrEmpty(presented)) return (null, null);
        var row = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == AuthTokens.Hash(presented), ct);
        if (row is null || row.ExpiresAt <= now) return (null, null);
        if (row.RevokedAt is { } revokedAt)
        {
            if (now - revokedAt <= Grace) return (null, null);
            await RevokeAllAsync(row.UserId, now, ct);
            return (null, null);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == row.UserId, ct);
        if (user is null) return (null, null);

        // Klaim atomik: dua permintaan serentak dengan token yang sama tidak boleh sama-sama menerbitkan token pengganti.
        // Yang kalah ditolak seperti tab kedua dalam tenggang (tanpa mencabut apa pun).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.RefreshTokens.Where(t => t.Id == row.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (claimed == 0) return (null, null);
        var token = AuthTokens.New();
        db.RefreshTokens.Add(Row(user.Id, token, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // ponytail: bersih-bersih baris kedaluwarsa menumpang rotasi, belum perlu job terjadwal.
        await db.RefreshTokens.Where(t => t.UserId == user.Id && t.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        return (user, token);
    }

    /// <summary>Logout: cabut token yang disajikan bila masih berlaku. Token asing tidak berefek apa pun.</summary>
    public async Task RevokeAsync(string? presented, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(presented)) return;
        var now = time.GetUtcNow().UtcDateTime;
        var hash = AuthTokens.Hash(presented);
        await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private Task<int> RevokeAllAsync(Guid userId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private RefreshToken Row(Guid userId, string token, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = AuthTokens.Hash(token),
        CreatedAt = now,
        ExpiresAt = now.AddDays(options.Value.RefreshDays),
    };
}
