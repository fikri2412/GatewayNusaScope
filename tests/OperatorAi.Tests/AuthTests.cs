using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using OperatorAi.Api.Auth;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

public class UserAccountsTests : QueueTestBase
{
    private UserAccounts Accounts(AppDbContext db) => new(db, new PasswordHasher<User>());

    [Fact]
    public async Task A_registered_user_can_log_in_and_the_password_is_stored_hashed()
    {
        await using var db = NewContext();
        var (registered, error) = await Accounts(db).RegisterAsync("  budi.santoso ", "rahasia-123", " budi.santoso@example.com ", CancellationToken.None);
        Assert.Null(error);

        var (user, loginError) = await Accounts(NewContext()).LoginAsync("budi.santoso", "rahasia-123", CancellationToken.None);
        Assert.Null(loginError);
        Assert.Equal(registered!.Id, user!.Id);
        Assert.Equal("budi.santoso", user.Username);
        Assert.DoesNotContain("rahasia-123", user.PasswordHash);
    }

    [Fact]
    public async Task Usernames_are_unique_regardless_of_letter_case()
    {
        await using var db = NewContext();
        Assert.Null((await Accounts(db).RegisterAsync("Budi", "rahasia-123", "budi@example.com", CancellationToken.None)).Error);

        var (user, error) = await Accounts(NewContext()).RegisterAsync("bUDI", "rahasia-456", "budi.lain@example.com", CancellationToken.None);

        Assert.Null(user);
        Assert.Equal(409, error!.Status);
        Assert.Equal(1, await NewContext().Users.CountAsync());
    }

    [Theory]
    [InlineData("ab", "rahasia-123")]                       // username terlalu pendek
    [InlineData("budi santoso", "rahasia-123")]             // spasi tidak diizinkan
    [InlineData("budi", "pendek")]                          // password < 8
    [InlineData(null, "rahasia-123")]
    [InlineData("budi", null)]
    public async Task Invalid_registration_input_is_rejected_with_400_and_nothing_is_stored(string? username, string? password)
    {
        await using var db = NewContext();

        var (user, error) = await Accounts(db).RegisterAsync(username, password, "budi@example.com", CancellationToken.None);

        Assert.Null(user);
        Assert.Equal(400, error!.Status);
        Assert.Equal(0, await NewContext().Users.CountAsync());
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_fail_identically()
    {
        await using var db = NewContext();
        await Accounts(db).RegisterAsync("budi", "rahasia-123", "budi@example.com", CancellationToken.None);

        var wrongPassword = await Accounts(NewContext()).LoginAsync("budi", "salah-banget", CancellationToken.None);
        var unknownUser = await Accounts(NewContext()).LoginAsync("tidak-ada", "rahasia-123", CancellationToken.None);
        var oversized = await Accounts(NewContext()).LoginAsync("budi", new string('x', UserAccounts.MaxPasswordLength + 1), CancellationToken.None);

        foreach (var (user, error) in new[] { wrongPassword, unknownUser, oversized })
        {
            Assert.Null(user);
            Assert.Equal(new AccountError(401, UserAccounts.BadCredentials, UserAccounts.InvalidCredentialsCode), error);
        }
    }
}

public class TokenIssuerTests
{
    private static readonly AuthOptions Cfg = new() { JwtKey = new string('k', 48), TokenMinutes = 60 };
    private static readonly User Budi = new() { Id = Guid.NewGuid(), Username = "budi", PasswordHash = "x" };

    private static TokenIssuer Issuer(AuthOptions o, TimeProvider? time = null) => new(Microsoft.Extensions.Options.Options.Create(o), time ?? TimeProvider.System);

    private static Task<Microsoft.IdentityModel.Tokens.TokenValidationResult> ValidateAsync(string token, AuthOptions o) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, TokenIssuer.Validation(o));

    [Fact]
    public async Task A_fresh_token_validates_and_carries_the_user_id_in_sub()
    {
        var (token, expires) = Issuer(Cfg).Create(Budi);

        var result = await ValidateAsync(token, Cfg);

        Assert.True(result.IsValid);
        Assert.Equal(Budi.Id.ToString(), result.Claims["sub"]);
        Assert.InRange(expires, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task Expired_tampered_and_foreign_key_tokens_are_rejected()
    {
        var (token, _) = Issuer(Cfg).Create(Budi);
        var longAgo = new FixedTime(DateTimeOffset.UtcNow.AddDays(-2));
        var (expired, _) = Issuer(Cfg, longAgo).Create(Budi);
        var (foreign, _) = Issuer(new AuthOptions { JwtKey = new string('z', 48), TokenMinutes = 60 }).Create(Budi);
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1][..^1]}{(parts[1][^1] == 'A' ? 'B' : 'A')}.{parts[2]}";   // payload diubah, tanda tangan lama

        foreach (var bad in new[] { expired, foreign, tampered })
            Assert.False((await ValidateAsync(bad, Cfg)).IsValid);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
