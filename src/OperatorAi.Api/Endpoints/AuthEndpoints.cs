using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OperatorAi.Api.Auth;
using OperatorAi.Api.Email;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;

namespace OperatorAi.Api;

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    /// <summary>Refresh/logout dipanggil browser berkali-kali (banyak tab) tetapi tidak menyentuh hash password.</summary>
    public const string RefreshRateLimitPolicy = "refresh";

    public const string RefreshCookie = "operator_ai_refresh";

    private const string CookiePath = "/api/auth";
    private const int MaxResetMailsPerHour = 3, MaxResetTokenLength = 128;
    private const string ResetMailSubject = "Atur ulang password NusaScope";

    public sealed record Credentials(string? Username, string? Password, bool? RememberMe);
    public sealed record RegisterRequest(string? Username, string? Password, string? Email, bool? RememberMe);
    public sealed record ForgotPasswordRequest(string? Email);
    public sealed record ResetPasswordRequest(string? Token, string? Password);
    public sealed record AuthResponse(string Token, string Username, DateTime ExpiresAt);

    /// <summary>Bentuk body gagal: teks Indonesia untuk user + kode mesin untuk klien.</summary>
    public sealed record AuthErrorBody(string Error, string Code);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").AllowAnonymous();
        g.MapPost("/register", RegisterAsync).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/forgot-password", ForgotPasswordAsync).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/reset-password", ResetPasswordAsync).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/refresh", RefreshAsync).RequireRateLimiting(RefreshRateLimitPolicy);
        g.MapPost("/logout", LogoutAsync).RequireRateLimiting(RefreshRateLimitPolicy);
    }

    public static async Task<IResult> RegisterAsync(HttpContext http, RegisterRequest? req, UserAccounts accounts,
        RefreshSessions sessions, TokenIssuer tokens, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var (user, error) = await accounts.RegisterAsync(req?.Username, req?.Password, req?.Email, ct);
        if (user is null) return Fail(error!);
        return Results.Json(await IssueAsync(http, user, req?.RememberMe == true, sessions, tokens, auth, ct),
            statusCode: StatusCodes.Status201Created);
    }

    public static async Task<IResult> LoginAsync(HttpContext http, Credentials? req, UserAccounts accounts,
        RefreshSessions sessions, TokenIssuer tokens, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var (user, error) = await accounts.LoginAsync(req?.Username, req?.Password, ct);
        if (user is null) return Fail(error!);
        return Results.Json(await IssueAsync(http, user, req?.RememberMe == true, sessions, tokens, auth, ct),
            statusCode: StatusCodes.Status200OK);
    }

    public static async Task<IResult> RefreshAsync(HttpContext http, RefreshSessions sessions, TokenIssuer tokens,
        IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var (user, rotated) = await sessions.RotateAsync(http.Request.Cookies[RefreshCookie], ct);
        if (user is null) return Fail(RefreshInvalid);
        http.Response.Cookies.Append(RefreshCookie, rotated!, Cookie(TimeSpan.FromDays(auth.Value.RefreshDays)));
        var (token, expires) = tokens.Create(user);
        return Results.Json(new AuthResponse(token, user.Username, expires), statusCode: StatusCodes.Status200OK);
    }

    public static async Task<IResult> LogoutAsync(HttpContext http, RefreshSessions sessions, CancellationToken ct)
    {
        // ponytail: token akses tetap sah sampai kedaluwarsa setelah logout, upgrade: daftar tolak jti.
        await sessions.RevokeAsync(http.Request.Cookies[RefreshCookie], ct);
        http.Response.Cookies.Delete(RefreshCookie, Cookie());
        return Results.NoContent();
    }

    /// <summary>
    /// Selalu 202 supaya tidak bisa dipakai menebak email terdaftar. Email dikirim lepas dari respons:
    /// kegagalan SMTP dicatat, tidak pernah terlihat user.
    /// </summary>
    public static async Task<IResult> ForgotPasswordAsync(ForgotPasswordRequest? req, AppDbContext db, IEmailSender email,
        ILoggerFactory loggers, IOptions<AuthOptions> auth, IOptions<WebOptions> web, IOptions<SmtpOptions> smtp,
        TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(smtp.Value.Host) || string.IsNullOrEmpty(web.Value.BaseUrl)) return Fail(ResetUnavailable);

        var now = time.GetUtcNow().UtcDateTime;
        var address = UserAccounts.NormalizeEmail(req?.Email);
        var user = address is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == address, ct);
        var recent = user is null
            ? 0
            : await db.PasswordResetTokens.CountAsync(t => t.UserId == user.Id && t.CreatedAt > now.AddMinutes(-60), ct);
        if (user is not null && recent < MaxResetMailsPerHour)
        {
            var token = AuthTokens.New();
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = AuthTokens.Hash(token),
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(auth.Value.ResetMinutes),
            });
            await db.SaveChangesAsync(ct);

            var to = user.Email!;
            var body = ResetMailBody(user.Username, web.Value.BaseUrl, token, auth.Value.ResetMinutes);
            var logger = loggers.CreateLogger("PasswordResetMail");
            var username = user.Username;
            // ponytail: kirim lepas dari respons (hilang bila proses mati di tengah jalan), naikkan ke tabel outbox bila butuh jaminan.
            _ = Task.Run(async () =>
            {
                try
                {
                    await email.SendAsync(to, ResetMailSubject, body);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Email atur ulang untuk {Username} gagal dikirim.", username);
                }
            });
        }
        return Results.Json(new { }, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>Token sekali pakai. Pemakaian sukses mencabut semua refresh token user dan mengganti SecurityStamp.</summary>
    public static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest? req, AppDbContext db,
        IPasswordHasher<User> hasher, TimeProvider time, CancellationToken ct)
    {
        var token = req?.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > MaxResetTokenLength) return Fail(InvalidResetToken);
        if (!UserAccounts.IsValidPassword(req?.Password)) return Fail(UserAccounts.PasswordError());

        var now = time.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rowId = await db.PasswordResetTokens.AsNoTracking().Where(t => t.TokenHash == AuthTokens.Hash(token))
            .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        if (rowId is null) return Fail(InvalidResetToken);

        // Klaim token satu kali: dua permintaan bersamaan hanya boleh satu yang berhasil.
        var claimed = await db.PasswordResetTokens.Where(t => t.Id == rowId && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (claimed != 1) return Fail(InvalidResetToken);

        var userId = await db.PasswordResetTokens.AsNoTracking().Where(t => t.Id == rowId).Select(t => t.UserId).SingleAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Fail(InvalidResetToken);

        user.PasswordHash = hasher.HashPassword(user, req!.Password!);
        user.SecurityStamp = Guid.NewGuid();
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.PasswordResetTokens.Where(t => t.UserId == userId && t.UsedAt == null && t.Id != rowId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<AuthResponse> IssueAsync(HttpContext http, User user, bool remember, RefreshSessions sessions,
        TokenIssuer tokens, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var (token, expires) = tokens.Create(user);
        if (remember)
            http.Response.Cookies.Append(RefreshCookie, await sessions.IssueAsync(user, ct),
                Cookie(TimeSpan.FromDays(auth.Value.RefreshDays)));
        else if (http.Request.Cookies.ContainsKey(RefreshCookie))
            // ponytail: token lama hanya dilepas dari browser dan kedaluwarsa sendiri; cabut barisnya di sini bila salinan token jadi ancaman nyata.
            http.Response.Cookies.Delete(RefreshCookie, Cookie());
        return new AuthResponse(token, user.Username, expires);
    }

    /// <param name="maxAge">Umur cookie; null = cookie sesi, sekaligus bentuk yang dipakai saat menghapus cookie.</param>
    private static CookieOptions Cookie(TimeSpan? maxAge = null) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        MaxAge = maxAge,
    };

    private static string ResetMailBody(string username, string baseUrl, string token, int minutes) =>
        $"""
        Halo {username},

        Ada permintaan atur ulang password untuk akun NusaScope Anda. Buka tautan berikut (berlaku {minutes} menit):

        {baseUrl}/?reset={token}

        Abaikan email ini jika bukan Anda yang meminta.
        """;

    private static IResult Fail(AccountError e) => Results.Json(new AuthErrorBody(e.Message, e.Code), statusCode: e.Status);

    private static readonly AccountError RefreshInvalid =
        new(401, "Sesi tidak berlaku. Silakan masuk lagi.", "refresh_invalid");
    private static readonly AccountError ResetUnavailable =
        new(503, "Atur ulang password belum tersedia di server ini.", "reset_unavailable");
    private static readonly AccountError InvalidResetToken =
        new(400, "Tautan atur ulang tidak berlaku atau sudah kedaluwarsa.", "invalid_or_expired_token");
}
