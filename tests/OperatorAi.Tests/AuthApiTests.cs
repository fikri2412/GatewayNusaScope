using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Api;
using OperatorAi.Api.Auth;

namespace OperatorAi.Tests;

/// <summary>
/// Permukaan auth yang dijaga untuk browser: pendaftaran dengan email, cookie refresh yang dirotasi,
/// dan penolakan pemakaian ulang token. Semua waktu diuji lewat stempel DB, bukan tidur.
/// </summary>
public class AuthApiTests : QueueTestBase
{
    [Fact]
    public async Task Registering_with_an_email_returns_201_and_stores_only_the_hashed_session()
    {
        var http = new DefaultHttpContext();
        var result = await AuthHarness.RegisterAsync(NewContext(), http, "  budi ", AuthHarness.Password,
            " Budi@Example.com ", rememberMe: true);

        Assert.Equal(201, AuthHarness.Status(result));
        Assert.Equal("budi", AuthHarness.Ok(result).Username);

        var raw = AuthHarness.CookieValue(http);
        Assert.NotNull(raw);

        await using var db = NewContext();
        var user = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal("budi@example.com", user.Email); // trim + lowercase
        Assert.NotEqual(Guid.Empty, user.SecurityStamp);

        var session = await db.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.Equal(user.Id, session.UserId);
        Assert.Equal(AuthTokens.Hash(raw!), session.TokenHash); // DB hanya menyimpan hash
        Assert.NotEqual(raw, session.TokenHash);
        Assert.Null(session.RevokedAt);
        Assert.InRange(session.ExpiresAt, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(30));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("budi")]
    [InlineData("budi@")]
    [InlineData("@example.com")]
    [InlineData("budi @example.com")]
    [InlineData("budi@example")]
    public async Task A_bad_email_is_rejected_with_invalid_email_and_nothing_is_stored(string? email)
    {
        var result = await AuthHarness.RegisterAsync(NewContext(), new DefaultHttpContext(), "budi", AuthHarness.Password, email);

        Assert.Equal(400, AuthHarness.Status(result));
        Assert.Equal("invalid_email", AuthHarness.Code(result));
        Assert.Equal(0, await NewContext().Users.CountAsync());
    }

    [Fact]
    public async Task An_email_over_254_characters_is_rejected()
    {
        var email = $"{new string('a', 246)}@example.com";

        var result = await AuthHarness.RegisterAsync(NewContext(), new DefaultHttpContext(), "budi", AuthHarness.Password, email);

        Assert.Equal(400, AuthHarness.Status(result));
        Assert.Equal("invalid_email", AuthHarness.Code(result));
    }

    [Fact]
    public async Task A_taken_email_is_rejected_with_email_taken_even_for_another_username()
    {
        Assert.Equal(201, AuthHarness.Status(await AuthHarness.RegisterAsync(NewContext(), new DefaultHttpContext(),
            "budi", AuthHarness.Password, "budi@example.com")));

        var result = await AuthHarness.RegisterAsync(NewContext(), new DefaultHttpContext(), "siti", AuthHarness.Password,
            " BUDI@example.com ");

        Assert.Equal(409, AuthHarness.Status(result));
        Assert.Equal("email_taken", AuthHarness.Code(result));
        Assert.Equal(1, await NewContext().Users.CountAsync());
    }

    [Fact]
    public async Task Refresh_rotates_the_session_and_the_rotated_out_token_stops_working()
    {
        await using var db = NewContext();
        var raw1 = await AuthHarness.RegisterRememberedAsync(NewContext());

        var rotate = AuthHarness.WithCookie(raw1);
        var result = await AuthHarness.RefreshAsync(NewContext(), rotate);

        Assert.Equal(200, AuthHarness.Status(result));
        var raw2 = AuthHarness.CookieValue(rotate);
        Assert.NotNull(raw2);
        Assert.NotEqual(raw1, raw2);
        Assert.NotEmpty(AuthHarness.Ok(result).Token);

        // Token lama sudah dirotasi; masih di dalam tenggang 10 detik, jadi hanya ditolak tanpa mencabut yang baru.
        var grace = await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie(raw1));
        Assert.Equal(401, AuthHarness.Status(grace));
        Assert.Equal("refresh_invalid", AuthHarness.Code(grace));

        var sessions = await db.RefreshTokens.AsNoTracking().ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, t => t.TokenHash == AuthTokens.Hash(raw1) && t.RevokedAt != null);
        Assert.Single(sessions, t => t.TokenHash == AuthTokens.Hash(raw2) && t.RevokedAt == null);

        var next = await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie(raw2));
        Assert.Equal(200, AuthHarness.Status(next));
    }

    [Fact]
    public async Task Reusing_a_rotated_token_after_the_grace_window_revokes_every_session()
    {
        var raw1 = await AuthHarness.RegisterRememberedAsync(NewContext());
        var rotate = AuthHarness.WithCookie(raw1);
        Assert.Equal(200, AuthHarness.Status(await AuthHarness.RefreshAsync(NewContext(), rotate)));
        var raw2 = AuthHarness.CookieValue(rotate)!;

        await using (var db = NewContext())
            await AuthHarness.AgeRefreshRevocationAsync(db, raw1, seconds: 15); // pemakaian ulang setelah tenggang

        var reuse = await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie(raw1));
        Assert.Equal(401, AuthHarness.Status(reuse));
        Assert.Equal("refresh_invalid", AuthHarness.Code(reuse));

        var after = await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie(raw2));
        Assert.Equal(401, AuthHarness.Status(after));

        await using var check = NewContext();
        Assert.Equal(0, await check.RefreshTokens.CountAsync(t => t.RevokedAt == null));
    }

    [Fact]
    public async Task An_unknown_expired_or_missing_refresh_token_is_rejected()
    {
        await using var db = NewContext();
        var raw = await AuthHarness.RegisterRememberedAsync(NewContext());
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE refresh_tokens SET expires_at = DATEADD(DAY, -1, SYSUTCDATETIME())");

        var expired = await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie(raw));
        var foreign = await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie("token-asing"));
        var missing = await AuthHarness.RefreshAsync(NewContext(), new DefaultHttpContext());

        foreach (var result in new[] { expired, foreign, missing })
        {
            Assert.Equal(401, AuthHarness.Status(result));
            Assert.Equal("refresh_invalid", AuthHarness.Code(result));
        }
    }

    [Fact]
    public async Task Logout_revokes_the_presented_session_and_always_clears_the_cookie()
    {
        var raw = await AuthHarness.RegisterRememberedAsync(NewContext());

        var http = AuthHarness.WithCookie(raw);
        var result = await AuthHarness.LogoutAsync(NewContext(), http);

        Assert.Equal(204, AuthHarness.Status(result));
        Assert.Equal("", AuthHarness.CookieValue(http));

        await using var db = NewContext();
        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.TokenHash == AuthTokens.Hash(raw) && t.RevokedAt != null));
        Assert.Equal(401, AuthHarness.Status(await AuthHarness.RefreshAsync(NewContext(), AuthHarness.WithCookie(raw))));

        var anonymous = new DefaultHttpContext();
        Assert.Equal(204, AuthHarness.Status(await AuthHarness.LogoutAsync(NewContext(), anonymous)));
        Assert.Equal("", AuthHarness.CookieValue(anonymous));
    }

    [Fact]
    public async Task Logging_in_without_remember_me_issues_no_session_and_clears_a_presented_cookie()
    {
        var raw = await AuthHarness.RegisterRememberedAsync(NewContext());

        var http = AuthHarness.WithCookie(raw);
        var result = await AuthHarness.LoginAsync(NewContext(), http, "budi", AuthHarness.Password);

        Assert.Equal(200, AuthHarness.Status(result));
        Assert.Equal("", AuthHarness.CookieValue(http));

        var remembered = AuthHarness.WithCookie(raw);
        Assert.Equal(200, AuthHarness.Status(await AuthHarness.LoginAsync(NewContext(), remembered, "budi",
            AuthHarness.Password, rememberMe: true)));
        Assert.NotNull(AuthHarness.CookieValue(remembered));
    }
}
