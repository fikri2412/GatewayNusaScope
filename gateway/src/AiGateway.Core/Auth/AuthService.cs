using System.Security.Cryptography;
using System.Text;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Auth;

public sealed record AuthUser(long Id, long? TenantId, string Email, string DisplayName, string Role, string SecurityStamp)
{
    public static AuthUser From(User u) => new(u.Id, u.TenantId, u.Email, u.DisplayName, u.Role, u.SecurityStamp);
}

/// <summary>Refresh token ditampilkan sekali; di database hanya hash-nya.</summary>
public sealed record LoginResult(AuthUser User, string RefreshToken, DateTime RefreshExpiresAt);

/// <summary>Login, rotasi refresh token, ganti password, dan token sekali pakai (undangan / reset).</summary>
public sealed class AuthService(
    GatewayDbContext db, IPasswordHasher<User> hasher, IOptions<AuthOptions> options, TimeProvider clock)
{
    // Hash palsu agar login untuk email tak dikenal memakan waktu yang sama dengan email yang dikenal.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<User>().HashPassword(new User { Email = "x", DisplayName = "x", PasswordHash = "", Role = Roles.Viewer }, "dummy-password-value"));
    private static readonly User DummyUser = new() { Email = "x", DisplayName = "x", PasswordHash = "", Role = Roles.Viewer };

    /// <summary>Jendela tenggang pemakaian ulang refresh token yang baru dirotasi (bukan pencurian).</summary>
    private static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(10);

    private AuthOptions Opt => options.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static GatewayException InvalidCredentials() =>
        new(401, "invalid_credentials", "Email atau password salah.");

    private static GatewayException InvalidUserToken() =>
        new(400, "invalid_token", "Token tidak valid atau sudah kedaluwarsa.");

    public async Task<LoginResult> LoginAsync(string email, string password, string? ip, string? userAgent, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null || !user.IsActive)
        {
            hasher.VerifyHashedPassword(DummyUser, DummyHash.Value, password ?? "");
            throw InvalidCredentials();
        }

        // Password diverifikasi lebih dulu: status terkunci hanya diberitahukan bila passwordnya benar, supaya
        // 429 tidak bisa dipakai untuk menebak email mana yang terdaftar (tak dikenal/nonaktif/salah = 401 sama).
        var locked = user.LockedUntil is { } until && until > Now;
        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (verification == PasswordVerificationResult.Failed)
        {
            // Percobaan selama masa kunci tidak menambah penghitung dan tidak memperpanjang kunci.
            if (!locked)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= Opt.MaxFailedLogins)
                {
                    user.LockedUntil = Now.AddMinutes(Opt.LockoutMinutes);
                    user.FailedLoginCount = 0;
                }
                await db.SaveChangesAsync(ct);
            }
            throw InvalidCredentials();
        }

        if (locked)
            throw new GatewayException(429, "account_locked", "Terlalu banyak percobaan gagal. Coba lagi nanti.");

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, password!);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = Now;
        var (token, expires) = AddRefreshToken(user.Id, ip, userAgent);
        await db.SaveChangesAsync(ct);
        return new LoginResult(AuthUser.From(user), token, expires);
    }

    /// <summary>
    /// Tukar refresh token dengan yang baru (sekali pakai). Token yang sudah diganti lalu dipakai lagi setelah
    /// jendela tenggang (10 detik) dianggap dicuri: semua refresh token pengguna dicabut.
    /// </summary>
    public async Task<LoginResult> RefreshAsync(string refreshToken, string? ip, string? userAgent, CancellationToken ct)
    {
        var invalid = new GatewayException(401, "invalid_refresh_token", "Sesi tidak valid atau sudah berakhir.");
        var hash = HashToken(refreshToken ?? "");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Klaim atomik: dua refresh serentak tidak boleh menerbitkan dua token pengganti.
        var claimed = await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > Now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), ct);
        if (claimed == 0)
        {
            var reused = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            // Pemakaian ulang dianggap pencurian hanya di luar jendela tenggang: klien yang mengulang permintaan
            // (respons hilang atau timeout) tidak boleh ikut mencabut seluruh sesi pengguna.
            if (reused?.ReplacedById is not null && reused.RevokedAt is { } revokedAt && Now - revokedAt > RefreshReuseGrace)
                await RevokeAllAsync(reused.UserId, ct);
            await tx.CommitAsync(ct);
            throw invalid;
        }
        var row = await db.RefreshTokens.FirstAsync(t => t.TokenHash == hash, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == row.UserId, ct);
        if (user is null || !user.IsActive)
        {
            // Klaim tetap di-commit: token yang sudah ditukar tidak boleh hidup lagi walau sesinya ditolak.
            await tx.CommitAsync(ct);
            throw invalid;
        }
        var (token, expires) = AddRefreshToken(user.Id, ip, userAgent);
        await db.SaveChangesAsync(ct);
        row.ReplacedById = await db.RefreshTokens.Where(t => t.TokenHash == HashToken(token)).Select(t => t.Id).FirstAsync(ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new LoginResult(AuthUser.From(user), token, expires);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = HashToken(refreshToken ?? "");
        await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), ct);
    }

    /// <summary>Dipakai validasi JWT: user harus aktif dan security stamp-nya masih sama.</summary>
    public async Task<AuthUser?> FindActiveAsync(long userId, string stamp, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is { IsActive: true } && user.SecurityStamp == stamp ? AuthUser.From(user) : null;
    }

    public async Task ChangePasswordAsync(long userId, string current, string next, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, current ?? "") == PasswordVerificationResult.Failed)
            throw new GatewayException(400, "wrong_password", "Password saat ini salah.");
        await SetPasswordAsync(user, next, ct);
    }

    /// <summary>Buat token sekali pakai (undangan/reset). Plaintext hanya dikembalikan di sini.</summary>
    public async Task<string> CreateUserTokenAsync(long userId, string purpose, TimeSpan ttl, CancellationToken ct)
    {
        var token = NewToken();
        db.UserTokens.Add(new UserToken { UserId = userId, Purpose = purpose, TokenHash = HashToken(token), ExpiresAt = Now.Add(ttl) });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>
    /// Pakai token undangan atau reset untuk mengatur password; sesi lama ikut dicabut. Mengembalikan pemilik
    /// token (id + tenant) supaya pemanggil bisa mencatatnya ke audit.
    /// </summary>
    public async Task<(long UserId, long? TenantId)> RedeemTokenAsync(string token, string purpose, string newPassword, CancellationToken ct)
    {
        var hash = HashToken(token ?? "");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.UserTokens.Where(t => t.TokenHash == hash && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > Now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, Now), ct);
        if (claimed == 0)
            throw InvalidUserToken();
        var userId = await db.UserTokens.Where(t => t.TokenHash == hash).Select(t => t.UserId).FirstAsync(ct);
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!user.IsActive)
        {
            // Akun nonaktif tidak boleh mengatur ulang password; token tetap dianggap terpakai (sekali pakai).
            await tx.CommitAsync(ct);
            throw InvalidUserToken();
        }
        await SetPasswordAsync(user, newPassword, ct);
        await tx.CommitAsync(ct);
        return (user.Id, user.TenantId);
    }

    private async Task SetPasswordAsync(User user, string password, CancellationToken ct)
    {
        ValidatePassword(password, user.Email);
        user.PasswordHash = hasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // membatalkan semua access token yang beredar
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        await RevokeAllAsync(user.Id, ct);
    }

    public void ValidatePassword(string password, string email)
    {
        if (password is null || password.Length < Opt.MinPasswordLength || password.Length > 128)
            throw GatewayException.BadRequest("weak_password", $"Password harus {Opt.MinPasswordLength}-128 karakter.");
        if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            throw GatewayException.BadRequest("weak_password", "Password tidak boleh sama dengan email.");
    }

    /// <summary>Password acak yang tidak pernah diketahui siapa pun; untuk akun yang menunggu undangan.</summary>
    public string UnusablePasswordHash(User user) => hasher.HashPassword(user, NewToken());

    private Task<int> RevokeAllAsync(long userId, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), ct);

    private (string Token, DateTime Expires) AddRefreshToken(long userId, string? ip, string? userAgent)
    {
        var token = NewToken();
        var expires = Now.AddDays(Opt.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId, TokenHash = HashToken(token), ExpiresAt = expires,
            Ip = ip is { Length: > 45 } ? ip[..45] : ip,
            UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
        });
        return (token, expires);
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
