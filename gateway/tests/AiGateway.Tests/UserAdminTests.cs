using AiGateway.Core.Auth;
using AiGateway.Core.Common;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

public class UserAdminTests(TestDb db) : GatewayTestBase(db)
{
    private Task<T> WithUsersAsync<T>(long tenantId, Func<UserAdminService, Task<T>> action) =>
        WithServicesAsync<T>(tenantId, (sp, _) => action(sp.GetRequiredService<UserAdminService>()));

    private static async Task<string> CodeOf(Func<Task> action) => (await Assert.ThrowsAsync<GatewayException>(action)).Code;

    [Fact]
    public async Task Invite_validates_input_and_emails_are_unique_across_tenants()
    {
        var a = await NewUserAsync();
        var b = await NewUserAsync();

        foreach (var bad in new[] { "", "not-an-email", "Name <x@example.test>", new string('a', 320) + "@example.test" })
            Assert.Equal("invalid_email", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.InviteAsync(bad, "N", Roles.Viewer, default))));
        Assert.Equal("invalid_name", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.InviteAsync("ok1@example.test", " ", Roles.Viewer, default))));
        Assert.Equal("invalid_role", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.InviteAsync("ok2@example.test", "N", Roles.PlatformAdmin, default))));
        Assert.Equal("invalid_role", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.InviteAsync("ok3@example.test", "N", "root", default))));

        // Email unik global: email milik tenant lain ditolak, tanpa membocorkan tenant mana pemiliknya.
        Assert.Equal("already_exists", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.InviteAsync(b.Email, "N", Roles.Viewer, default))));
        Assert.Equal("already_exists", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.InviteAsync(a.Email.ToUpperInvariant(), "N", Roles.Viewer, default))));
    }

    [Fact]
    public async Task Listing_and_updating_are_confined_to_the_callers_tenant()
    {
        var a = await NewUserAsync();
        var b = await NewUserAsync();

        var listed = await WithUsersAsync(a.TenantId, u => u.ListAsync(default));
        Assert.Equal([a.UserId], listed.Select(x => x.Id).ToArray());

        Assert.Equal("not_found", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.UpdateAsync(b.UserId, Roles.Viewer, null, a.UserId, default))));
        Assert.Equal("not_found", await CodeOf(() => WithUsersAsync(a.TenantId, u => u.IssueResetTokenAsync(b.UserId, default))));
    }

    [Fact]
    public async Task Users_cannot_change_themselves_or_remove_the_last_active_owner()
    {
        var owner = await NewUserAsync(Roles.Owner);
        Assert.Equal("self_change", await CodeOf(() => WithUsersAsync(owner.TenantId, u => u.UpdateAsync(owner.UserId, Roles.Viewer, null, owner.UserId, default))));

        var second = await NewUserAsync(Roles.Owner, owner.TenantId);
        // Dua owner: owner kedua boleh diturunkan oleh owner pertama...
        await WithUsersAsync(owner.TenantId, u => u.UpdateAsync(second.UserId, Roles.Admin, null, owner.UserId, default));
        // ...tetapi owner terakhir tidak boleh dihilangkan lewat peran maupun penonaktifan.
        var other = await NewUserAsync(Roles.Admin, owner.TenantId);
        Assert.Equal("last_owner", await CodeOf(() => WithUsersAsync(owner.TenantId, u => u.UpdateAsync(owner.UserId, null, false, other.UserId, default))));
        Assert.Equal("last_owner", await CodeOf(() => WithUsersAsync(owner.TenantId, u => u.UpdateAsync(owner.UserId, Roles.Admin, null, other.UserId, default))));
    }

    [Fact]
    public async Task Changing_role_or_deactivating_rotates_the_stamp_and_revokes_sessions()
    {
        var owner = await NewUserAsync(Roles.Owner);
        var member = await NewUserAsync(Roles.Admin, owner.TenantId);
        var session = await WithServicesAsync<LoginResult>(null, (sp, _) => sp.GetRequiredService<AuthService>().LoginAsync(member.Email, StrongPassword, null, null, default));

        var updated = await WithUsersAsync(owner.TenantId, u => u.UpdateAsync(member.UserId, Roles.Viewer, null, owner.UserId, default));

        Assert.Equal(Roles.Viewer, updated.Role);
        Assert.NotEqual(session.User.SecurityStamp, updated.SecurityStamp);
        Assert.Null(await WithServicesAsync<AuthUser?>(null, (sp, _) => sp.GetRequiredService<AuthService>().FindActiveAsync(member.UserId, session.User.SecurityStamp, default)));
        await using var ctx = Db.NewContext();
        Assert.Equal(0, await ctx.RefreshTokens.CountAsync(t => t.UserId == member.UserId && t.RevokedAt == null));

        await WithUsersAsync(owner.TenantId, u => u.UpdateAsync(member.UserId, null, false, owner.UserId, default));
        var login = await Assert.ThrowsAsync<GatewayException>(() =>
            WithServicesAsync<LoginResult>(null, (sp, _) => sp.GetRequiredService<AuthService>().LoginAsync(member.Email, StrongPassword, null, null, default)));
        Assert.Equal("invalid_credentials", login.Code);
    }

    [Fact]
    public async Task Reset_tokens_are_only_issued_for_active_users_of_the_tenant()
    {
        var owner = await NewUserAsync(Roles.Owner);
        var member = await NewUserAsync(Roles.Viewer, owner.TenantId);

        var token = await WithUsersAsync(owner.TenantId, u => u.IssueResetTokenAsync(member.UserId, default));
        Assert.False(string.IsNullOrWhiteSpace(token));
        // hanya hash yang tersimpan
        await using var ctx = Db.NewContext();
        Assert.True(await ctx.UserTokens.AnyAsync(t => t.TokenHash == AuthService.HashToken(token) && t.Purpose == TokenPurposes.ResetPassword));
        Assert.False(await ctx.UserTokens.AnyAsync(t => t.TokenHash == token));

        await WithUsersAsync(owner.TenantId, u => u.UpdateAsync(member.UserId, null, false, owner.UserId, default));
        Assert.Equal("not_found", await CodeOf(() => WithUsersAsync(owner.TenantId, u => u.IssueResetTokenAsync(member.UserId, default))));
    }

    [Fact]
    public async Task The_service_refuses_to_run_without_a_tenant_context()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithServicesAsync<List<User>>(null, (sp, _) => sp.GetRequiredService<UserAdminService>().ListAsync(default)));
    }

    [Fact]
    public async Task Listing_users_is_capped_at_500_rows()
    {
        var owner = await NewUserAsync(Roles.Owner);
        await using (var ctx = Db.NewContext(owner.TenantId))
        {
            ctx.Users.AddRange(Enumerable.Range(0, 501).Select(i => new User
            {
                TenantId = owner.TenantId, Email = $"bulk-{i:D4}@example.test", DisplayName = "Bulk",
                PasswordHash = "x", Role = Roles.Viewer,
            }));
            await ctx.SaveChangesAsync();
        }

        var listed = await WithUsersAsync(owner.TenantId, u => u.ListAsync(default));
        Assert.Equal(500, listed.Count);
    }

    [Fact]
    public async Task Revocation_timestamps_use_the_injected_clock()
    {
        var owner = await NewUserAsync(Roles.Owner);
        var member = await NewUserAsync(Roles.Admin, owner.TenantId);
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        await using var ctx = Db.NewContext(owner.TenantId);
        var auth = new AuthService(ctx, new PasswordHasher<User>(), Factory.Services.GetRequiredService<IOptions<AuthOptions>>(), clock);
        var users = new UserAdminService(ctx, auth, clock);
        var session = new RefreshToken
        {
            UserId = member.UserId, TokenHash = AuthService.HashToken("seed-refresh"), ExpiresAt = clock.Now.UtcDateTime.AddDays(1),
        };
        ctx.RefreshTokens.Add(session);
        await ctx.SaveChangesAsync();

        await users.UpdateAsync(member.UserId, Roles.Viewer, null, owner.UserId, default);

        var revokedAt = (await ctx.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == session.Id)).RevokedAt;
        Assert.Equal<DateTime?>(clock.Now.UtcDateTime, revokedAt);
    }

    /// <summary>Jam manual untuk test yang membutuhkan waktu beku.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
