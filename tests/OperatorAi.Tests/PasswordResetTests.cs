using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using OperatorAi.Api.Auth;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

/// <summary>
/// Lupa dan atur ulang password: jawaban yang sama untuk email terdaftar maupun tidak, tautan sekali pakai,
/// dan sesi lama yang langsung mati setelah password diganti.
/// </summary>
public class PasswordResetApiTests : QueueTestBase
{
    private static readonly SmtpOptions Smtp = new() { Host = "smtp.example.com", From = "noreply@example.com" };
    private static readonly WebOptions Web = new() { BaseUrl = "http://localhost:5175" };

    [Fact]
    public async Task Forgot_password_answers_202_and_mails_only_the_registered_address()
    {
        var (userId, _) = await RegisterAsync();
        var email = new RecordingEmail();

        var known = await AuthHarness.ForgotPasswordAsync(NewContext(), email, Smtp, Web, " BUDI@example.com ");
        Assert.Equal(202, AuthHarness.Status(known));

        var sent = await ReadAsync(email);
        Assert.Equal(AuthHarness.Email, sent.To);
        Assert.Equal("Atur ulang password NusaScope", sent.Subject);
        Assert.Contains("budi", sent.Body);
        Assert.Contains("http://localhost:5175/?reset=", sent.Body); // Web:BaseUrl tanpa garis miring ganda
        Assert.Contains("30 menit", sent.Body);
        Assert.Contains("abaikan", sent.Body, StringComparison.OrdinalIgnoreCase);

        // Alamat asing: jawaban sama, tanpa email dan tanpa token.
        var unknown = await AuthHarness.ForgotPasswordAsync(NewContext(), email, Smtp, Web, "tidak-ada@example.com");
        Assert.Equal(202, AuthHarness.Status(unknown));
        Assert.False(email.Sent.TryRead(out _));

        await using var db = NewContext();
        var row = Assert.Single(await db.PasswordResetTokens.AsNoTracking().ToListAsync());
        Assert.Equal(userId, row.UserId);
        Assert.Equal(AuthTokens.Hash(ExtractToken(sent.Body)), row.TokenHash); // yang disimpan hanya hash
        Assert.Null(row.UsedAt);
        Assert.InRange(row.ExpiresAt, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(30));
    }

    [Theory]
    [InlineData("smtp-host")]
    [InlineData("web-base-url")]
    public async Task Forgot_password_is_unavailable_without_smtp_host_or_web_base_url(string missing)
    {
        await RegisterAsync();
        var email = new RecordingEmail();
        var smtp = missing == "smtp-host" ? new SmtpOptions() : Smtp;
        var web = missing == "web-base-url" ? new WebOptions() : Web;

        var result = await AuthHarness.ForgotPasswordAsync(NewContext(), email, smtp, web, AuthHarness.Email);

        Assert.Equal(503, AuthHarness.Status(result));
        Assert.Equal("reset_unavailable", AuthHarness.Code(result));
        Assert.False(email.Sent.TryRead(out _));
        Assert.Equal(0, await NewContext().PasswordResetTokens.CountAsync());
    }

    [Fact]
    public async Task Reset_password_consumes_the_token_once_and_replaces_the_password()
    {
        await RegisterAsync();
        var token = await RequestResetAsync();

        Assert.Equal(204, AuthHarness.Status(await AuthHarness.ResetPasswordAsync(NewContext(), token, AuthHarness.NewPassword)));
        Assert.NotNull((await NewContext().PasswordResetTokens.AsNoTracking().SingleAsync()).UsedAt);
        Assert.Null((await AuthHarness.Accounts(NewContext()).LoginAsync("budi", AuthHarness.NewPassword, CancellationToken.None)).Error);

        var (oldUser, oldError) = await AuthHarness.Accounts(NewContext()).LoginAsync("budi", AuthHarness.Password, CancellationToken.None);
        Assert.Null(oldUser);
        Assert.Equal(401, oldError!.Status);

        // Tautan yang sama tidak bisa dipakai dua kali, dan percobaan itu tidak mengubah password.
        var replay = await AuthHarness.ResetPasswordAsync(NewContext(), token, "password-lain");
        Assert.Equal(400, AuthHarness.Status(replay));
        Assert.Equal("invalid_or_expired_token", AuthHarness.Code(replay));
        Assert.Null((await AuthHarness.Accounts(NewContext()).LoginAsync("budi", AuthHarness.NewPassword, CancellationToken.None)).Error);

        Assert.Equal("invalid_or_expired_token",
            AuthHarness.Code(await AuthHarness.ResetPasswordAsync(NewContext(), "token-asing", AuthHarness.NewPassword)));
        Assert.Equal("invalid_or_expired_token",
            AuthHarness.Code(await AuthHarness.ResetPasswordAsync(NewContext(), null, AuthHarness.NewPassword)));
        Assert.Equal("invalid_password",
            AuthHarness.Code(await AuthHarness.ResetPasswordAsync(NewContext(), await RequestResetAsync(), "pendek")));
    }

    [Fact]
    public async Task An_expired_reset_token_is_rejected_without_changing_the_password()
    {
        await RegisterAsync();
        var token = await RequestResetAsync();
        await using (var db = NewContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE password_reset_tokens SET expires_at = DATEADD(MINUTE, -5, SYSUTCDATETIME())");

        var result = await AuthHarness.ResetPasswordAsync(NewContext(), token, AuthHarness.NewPassword);

        Assert.Equal(400, AuthHarness.Status(result));
        Assert.Equal("invalid_or_expired_token", AuthHarness.Code(result));
        Assert.Null((await AuthHarness.Accounts(NewContext()).LoginAsync("budi", AuthHarness.Password, CancellationToken.None)).Error);
        Assert.Null((await NewContext().PasswordResetTokens.AsNoTracking().SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task Reset_password_revokes_refresh_sessions_and_invalidates_old_access_tokens()
    {
        var (userId, refresh) = await RegisterAsync(remember: true);
        var user = await NewContext().Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        var (oldToken, _) = AuthHarness.Tokens().Create(user);
        var oldPrincipal = await PrincipalAsync(oldToken);
        Assert.True(await SessionGuard.IsValidAsync(oldPrincipal, NewContext(), CancellationToken.None));

        var token = await RequestResetAsync();
        Assert.Equal(204, AuthHarness.Status(await AuthHarness.ResetPasswordAsync(NewContext(), token, AuthHarness.NewPassword)));

        await using var db = NewContext();
        Assert.Equal(0, await db.RefreshTokens.CountAsync(t => t.RevokedAt == null));
        Assert.Equal(401, AuthHarness.Status(await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie(refresh!))));
        Assert.False(await SessionGuard.IsValidAsync(oldPrincipal, NewContext(), CancellationToken.None));

        // Token yang terbit setelah atur ulang tetap diterima: yang berubah hanya SecurityStamp-nya.
        var current = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.NotEqual(user.SecurityStamp, current.SecurityStamp);
        var (freshToken, _) = AuthHarness.Tokens().Create(current);
        Assert.True(await SessionGuard.IsValidAsync(await PrincipalAsync(freshToken), NewContext(), CancellationToken.None));
    }

    private async Task<(Guid UserId, string? Refresh)> RegisterAsync(bool remember = false)
    {
        var http = new DefaultHttpContext();
        var result = await AuthHarness.RegisterAsync(NewContext(), http, "budi", AuthHarness.Password, AuthHarness.Email, remember);
        Assert.Equal(201, AuthHarness.Status(result));
        return ((await NewContext().Users.AsNoTracking().SingleAsync()).Id, AuthHarness.CookieValue(http));
    }

    private async Task<string> RequestResetAsync()
    {
        var email = new RecordingEmail();
        var result = await AuthHarness.ForgotPasswordAsync(NewContext(), email, Smtp, Web, AuthHarness.Email);
        Assert.Equal(202, AuthHarness.Status(result));
        return ExtractToken((await ReadAsync(email)).Body);
    }

    private static async Task<(string To, string Subject, string Body)> ReadAsync(RecordingEmail email)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await email.Sent.ReadAsync(timeout.Token); // menunggu kiriman latar, bukan tidur
    }

    private static string ExtractToken(string body) => body.Split("?reset=")[1].Split('\n')[0].Trim();

    private static async Task<ClaimsPrincipal> PrincipalAsync(string token)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, TokenIssuer.Validation(AuthHarness.Cfg));
        Assert.True(result.IsValid);
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }
}
