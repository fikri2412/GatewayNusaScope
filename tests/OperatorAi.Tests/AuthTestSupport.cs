using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OperatorAi.Api;
using OperatorAi.Api.Auth;
using OperatorAi.Api.Email;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

/// <summary>
/// Pemanggil endpoint auth seperti satu permintaan HTTP: <see cref="DefaultHttpContext"/> membawa cookie,
/// hasilnya dibaca sebagai status + body + Set-Cookie. Satu <see cref="AppDbContext"/> per panggilan.
/// </summary>
internal static class AuthHarness
{
    public const string Password = "rahasia-123", Email = "budi@example.com";
    public const string NewPassword = "password-baru";

    public static readonly AuthOptions Cfg = new() { JwtKey = new string('k', 48), TokenMinutes = 60 };

    private static readonly IPasswordHasher<User> Hasher = new PasswordHasher<User>();

    public static IOptions<AuthOptions> Options => Microsoft.Extensions.Options.Options.Create(Cfg);

    public static UserAccounts Accounts(AppDbContext db) => new(db, Hasher);

    public static RefreshSessions Sessions(AppDbContext db) => new(db, TimeProvider.System, Options);

    public static TokenIssuer Tokens() => new(Options, TimeProvider.System);

    public static Task<IResult> RegisterAsync(AppDbContext db, HttpContext http, string? username, string? password,
        string? email, bool? rememberMe = null) => AuthEndpoints.RegisterAsync(http, new(username, password, email, rememberMe),
            Accounts(db), Sessions(db), Tokens(), Options, CancellationToken.None);

    public static Task<IResult> LoginAsync(AppDbContext db, HttpContext http, string? username, string? password,
        bool? rememberMe = null) => AuthEndpoints.LoginAsync(http, new(username, password, rememberMe),
            Accounts(db), Sessions(db), Tokens(), Options, CancellationToken.None);

    public static Task<IResult> RefreshAsync(AppDbContext db, HttpContext http) =>
        AuthEndpoints.RefreshAsync(http, Sessions(db), Tokens(), Options, CancellationToken.None);

    public static Task<IResult> LogoutAsync(AppDbContext db, HttpContext http) =>
        AuthEndpoints.LogoutAsync(http, Sessions(db), CancellationToken.None);

    public static Task<IResult> ForgotPasswordAsync(AppDbContext db, IEmailSender email, SmtpOptions smtp, WebOptions web,
        string? address) => AuthEndpoints.ForgotPasswordAsync(new(address), db, email, NullLoggerFactory.Instance,
            Options, Microsoft.Extensions.Options.Options.Create(web), Microsoft.Extensions.Options.Options.Create(smtp),
            TimeProvider.System, CancellationToken.None);

    public static Task<IResult> ResetPasswordAsync(AppDbContext db, string? token, string? password) =>
        AuthEndpoints.ResetPasswordAsync(new(token, password), db, Hasher, TimeProvider.System, CancellationToken.None);

    public static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;

    public static string Code(IResult result) => ((JsonHttpResult<AuthEndpoints.AuthErrorBody>)result).Value!.Code;

    public static AuthEndpoints.AuthResponse Ok(IResult result) => ((JsonHttpResult<AuthEndpoints.AuthResponse>)result).Value!;

    /// <summary>Permintaan yang membawa cookie refresh, seperti browser setelah "ingat saya".</summary>
    public static DefaultHttpContext WithCookie(string? raw)
    {
        var http = new DefaultHttpContext();
        if (raw is not null) http.Request.Headers.Cookie = $"{AuthEndpoints.RefreshCookie}={raw}";
        return http;
    }

    /// <summary>Nilai cookie dari Set-Cookie respons; "" berarti cookie dihapus.</summary>
    public static string? CookieValue(HttpContext http)
    {
        foreach (var header in http.Response.Headers.SetCookie)
        {
            var pair = header?.Split(';').FirstOrDefault(p => p.StartsWith($"{AuthEndpoints.RefreshCookie}=", StringComparison.Ordinal));
            if (pair is not null) return pair[(AuthEndpoints.RefreshCookie.Length + 1)..];
        }
        return null;
    }

    /// <summary>Daftar dengan "ingat saya"; mengembalikan token mentah dari cookie yang dipasang.</summary>
    public static async Task<string> RegisterRememberedAsync(AppDbContext db)
    {
        var http = new DefaultHttpContext();
        var result = await RegisterAsync(db, http, "budi", Password, Email, rememberMe: true);
        Assert.Equal(201, Status(result));
        return CookieValue(http)!;
    }

    /// <summary>Memundurkan waktu pencabutan token di DB (bukan tidur) supaya tenggang 10 detik bisa diuji.</summary>
    public static Task AgeRefreshRevocationAsync(AppDbContext db, string token, int seconds) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE refresh_tokens SET revoked_at = DATEADD(SECOND, -{0}, SYSUTCDATETIME()) WHERE token_hash = {1}",
            seconds, AuthTokens.Hash(token));
}

/// <summary>Penyadap email: test menunggu kiriman berikutnya tanpa tidur.</summary>
internal sealed class RecordingEmail : IEmailSender
{
    private readonly Channel<(string To, string Subject, string Body)> _sent = Channel.CreateUnbounded<(string, string, string)>();

    public ChannelReader<(string To, string Subject, string Body)> Sent => _sent.Reader;

    public Task SendAsync(string to, string subject, string body)
    {
        _sent.Writer.TryWrite((to, subject, body)); // unbounded: selalu berhasil
        return Task.CompletedTask;
    }
}
