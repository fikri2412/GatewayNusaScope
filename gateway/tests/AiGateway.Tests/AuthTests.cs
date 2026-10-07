using AiGateway.Core.Auth;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

public class AuthTests(TestDb db) : GatewayTestBase(db)
{
    private Task WithAuthAsync(Func<AuthService, GatewayDbContext, Task> action) =>
        WithServicesAsync<int>(null, async (sp, ctx) => { await action(sp.GetRequiredService<AuthService>(), ctx); return 0; });

    private Task<T> WithAuthAsync<T>(Func<AuthService, GatewayDbContext, Task<T>> action) =>
        WithServicesAsync<T>(null, (sp, ctx) => action(sp.GetRequiredService<AuthService>(), ctx));

    private static async Task<string> CodeOf(Func<Task> action) => (await Assert.ThrowsAsync<GatewayException>(action)).Code;

    [Fact]
    public async Task Login_issues_a_refresh_token_that_is_stored_only_as_a_hash()
    {
        var user = await NewUserAsync(Roles.Admin);

        var result = await WithAuthAsync((auth, _) => auth.LoginAsync(user.Email.ToUpperInvariant(), StrongPassword, "203.0.113.9", "test-agent", default));

        Assert.Equal((user.UserId, user.TenantId, Roles.Admin), (result.User.Id, result.User.TenantId, result.User.Role));
        await using var ctx = Db.NewContext();
        var row = await ctx.RefreshTokens.SingleAsync(t => t.UserId == user.UserId);
        Assert.Equal(AuthService.HashToken(result.RefreshToken), row.TokenHash);
        Assert.NotEqual(result.RefreshToken, row.TokenHash);
        Assert.Equal(("203.0.113.9", "test-agent"), (row.Ip, row.UserAgent));
        Assert.NotNull((await ctx.Users.SingleAsync(u => u.Id == user.UserId)).LastLoginAt);
    }

    [Fact]
    public async Task Unknown_inactive_and_wrong_password_logins_are_indistinguishable()
    {
        var user = await NewUserAsync();
        await using (var ctx = Db.NewContext())
        {
            var inactive = await NewUserAsync(tenantId: user.TenantId);
            await ctx.Users.Where(u => u.Id == inactive.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));

            foreach (var email in new[] { "nobody@example.test", inactive.Email })
            {
                var ex = await Assert.ThrowsAsync<GatewayException>(() => WithAuthAsync((a, _) => a.LoginAsync(email, StrongPassword, null, null, default)));
                Assert.Equal(("invalid_credentials", 401), (ex.Code, ex.Status));
            }
        }

        var wrong = await Assert.ThrowsAsync<GatewayException>(() => WithAuthAsync((a, _) => a.LoginAsync(user.Email, "wrong-password-123", null, null, default)));
        Assert.Equal("invalid_credentials", wrong.Code);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_right_password_until_it_expires()
    {
        var user = await NewUserAsync();
        var max = Factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value.MaxFailedLogins;

        for (var i = 0; i < max; i++)
            Assert.Equal("invalid_credentials", await CodeOf(() => WithAuthAsync((a, _) => a.LoginAsync(user.Email, "wrong-password-123", null, null, default))));

        var locked = await Assert.ThrowsAsync<GatewayException>(() => WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default)));
        Assert.Equal((429, "account_locked"), (locked.Status, locked.Code));

        await using var ctx = Db.NewContext();
        await ctx.Users.Where(u => u.Id == user.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.LockedUntil, DateTime.UtcNow.AddMinutes(-1)));
        var ok = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default));
        Assert.Equal(user.UserId, ok.User.Id);
        Assert.Equal(0, (await ctx.Users.AsNoTracking().SingleAsync(u => u.Id == user.UserId)).FailedLoginCount);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reusing_an_old_one_revokes_the_whole_session()
    {
        var user = await NewUserAsync();
        var first = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default));

        var second = await WithAuthAsync((a, _) => a.RefreshAsync(first.RefreshToken, null, null, default));
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        Assert.Equal("invalid_refresh_token", await CodeOf(() => WithAuthAsync((a, _) => a.RefreshAsync(first.RefreshToken, null, null, default))));
        // pemakaian ulang token lama dianggap pencurian: token baru ikut mati
        Assert.Equal("invalid_refresh_token", await CodeOf(() => WithAuthAsync((a, _) => a.RefreshAsync(second.RefreshToken, null, null, default))));
    }

    [Fact]
    public async Task Refresh_rejects_unknown_expired_logged_out_and_deactivated_sessions()
    {
        var user = await NewUserAsync();
        Assert.Equal("invalid_refresh_token", await CodeOf(() => WithAuthAsync((a, _) => a.RefreshAsync("not-a-token", null, null, default))));

        var expired = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default));
        await using var ctx = Db.NewContext();
        await ctx.RefreshTokens.Where(t => t.TokenHash == AuthService.HashToken(expired.RefreshToken))
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal("invalid_refresh_token", await CodeOf(() => WithAuthAsync((a, _) => a.RefreshAsync(expired.RefreshToken, null, null, default))));

        var loggedOut = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default));
        await WithAuthAsync(async (a, _) => { await a.LogoutAsync(loggedOut.RefreshToken, default); return 0; });
        Assert.Equal("invalid_refresh_token", await CodeOf(() => WithAuthAsync((a, _) => a.RefreshAsync(loggedOut.RefreshToken, null, null, default))));

        var deactivated = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default));
        await ctx.Users.Where(u => u.Id == user.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        Assert.Equal("invalid_refresh_token", await CodeOf(() => WithAuthAsync((a, _) => a.RefreshAsync(deactivated.RefreshToken, null, null, default))));
    }

    [Fact]
    public async Task Changing_the_password_invalidates_old_sessions_and_access_tokens()
    {
        var user = await NewUserAsync();
        var session = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default));

        Assert.Equal("wrong_password", await CodeOf(() => WithAuthAsync((a, _) => a.ChangePasswordAsync(user.UserId, "nope-nope-nope-1", "another-strong-pass-9", default))));
        Assert.Equal("weak_password", await CodeOf(() => WithAuthAsync((a, _) => a.ChangePasswordAsync(user.UserId, StrongPassword, "short", default))));

        await WithAuthAsync(async (a, _) => { await a.ChangePasswordAsync(user.UserId, StrongPassword, "another-strong-pass-9", default); return 0; });

        Assert.Null(await WithAuthAsync((a, _) => a.FindActiveAsync(user.UserId, session.User.SecurityStamp, default)));
        Assert.Equal("invalid_refresh_token", await CodeOf(() => WithAuthAsync((a, _) => a.RefreshAsync(session.RefreshToken, null, null, default))));
        Assert.Equal("invalid_credentials", await CodeOf(() => WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default))));
        var again = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, "another-strong-pass-9", null, null, default));
        Assert.NotNull(await WithAuthAsync((a, _) => a.FindActiveAsync(user.UserId, again.User.SecurityStamp, default)));
    }

    [Fact]
    public async Task Access_token_validation_requires_an_active_user_with_the_current_stamp()
    {
        var user = await NewUserAsync();
        var login = await WithAuthAsync((a, _) => a.LoginAsync(user.Email, StrongPassword, null, null, default));

        Assert.NotNull(await WithAuthAsync((a, _) => a.FindActiveAsync(user.UserId, login.User.SecurityStamp, default)));
        Assert.Null(await WithAuthAsync((a, _) => a.FindActiveAsync(user.UserId, "stale-stamp", default)));

        await using var ctx = Db.NewContext();
        await ctx.Users.Where(u => u.Id == user.UserId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        Assert.Null(await WithAuthAsync((a, _) => a.FindActiveAsync(user.UserId, login.User.SecurityStamp, default)));
    }

    [Fact]
    public async Task Invite_and_reset_tokens_are_single_use_purpose_bound_and_expire()
    {
        var user = await NewUserAsync();
        var invited = await NewUserAsync(tenantId: user.TenantId);

        // Token undangan sudah dipakai di NewUserAsync: tidak bisa dipakai lagi.
        var reset = await WithServicesAsync(user.TenantId, async (sp, _) =>
            await sp.GetRequiredService<UserAdminService>().IssueResetTokenAsync(invited.UserId, default));

        Assert.Equal("invalid_token", await CodeOf(() => WithAuthAsync((a, _) => a.RedeemTokenAsync(reset, TokenPurposes.Invite, "fresh-strong-pass-77", default)))); // salah tujuan
        Assert.Equal("weak_password", await CodeOf(() => WithAuthAsync((a, _) => a.RedeemTokenAsync(reset, TokenPurposes.ResetPassword, "short", default))));
        await WithAuthAsync(async (a, _) => { await a.RedeemTokenAsync(reset, TokenPurposes.ResetPassword, "fresh-strong-pass-77", default); return 0; });
        Assert.Equal("invalid_token", await CodeOf(() => WithAuthAsync((a, _) => a.RedeemTokenAsync(reset, TokenPurposes.ResetPassword, "fresh-strong-pass-78", default)))); // sekali pakai

        var expiring = await WithServicesAsync(user.TenantId, async (sp, _) =>
            await sp.GetRequiredService<UserAdminService>().IssueResetTokenAsync(invited.UserId, default));
        await using var ctx = Db.NewContext();
        await ctx.UserTokens.Where(t => t.TokenHash == AuthService.HashToken(expiring))
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal("invalid_token", await CodeOf(() => WithAuthAsync((a, _) => a.RedeemTokenAsync(expiring, TokenPurposes.ResetPassword, "fresh-strong-pass-79", default))));

        var ok = await WithAuthAsync((a, _) => a.LoginAsync(invited.Email, "fresh-strong-pass-77", null, null, default));
        Assert.Equal(invited.UserId, ok.User.Id);
    }

    [Fact]
    public async Task An_invited_account_cannot_log_in_before_accepting_the_invitation()
    {
        var user = await NewUserAsync();
        var email = $"{Guid.NewGuid():N}@example.test";
        await WithServicesAsync(user.TenantId, async (sp, _) => await sp.GetRequiredService<UserAdminService>().InviteAsync(email, "Pending", Roles.Viewer, default));

        Assert.Equal("invalid_credentials", await CodeOf(() => WithAuthAsync((a, _) => a.LoginAsync(email, "anything-at-all-123", null, null, default))));
    }

    [Theory]
    [InlineData("short1")]
    [InlineData("")]
    public async Task Password_policy_rejects_short_passwords(string password)
    {
        await WithAuthAsync((auth, _) =>
        {
            Assert.Equal("weak_password", Assert.Throws<GatewayException>(() => auth.ValidatePassword(password, "a@example.test")).Code);
            return Task.FromResult(0);
        });
    }

    [Fact]
    public async Task Password_policy_rejects_the_email_and_overlong_values_but_accepts_a_strong_one()
    {
        await WithAuthAsync((auth, _) =>
        {
            Assert.Equal("weak_password", Assert.Throws<GatewayException>(() => auth.ValidatePassword("someone@example.test", "someone@example.test")).Code);
            Assert.Equal("weak_password", Assert.Throws<GatewayException>(() => auth.ValidatePassword(new string('a', 129), "a@example.test")).Code);
            auth.ValidatePassword(StrongPassword, "a@example.test");
            return Task.FromResult(0);
        });
    }
}
