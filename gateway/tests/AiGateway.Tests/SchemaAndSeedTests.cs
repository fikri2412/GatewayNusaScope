using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

public class SchemaAndSeedTests(TestDb testDb) : IClassFixture<TestDb>
{
    [Fact]
    public async Task Seeder_is_idempotent_and_hashes_the_password()
    {
        var seed = Options.Create(new SeedOptions { AdminEmail = "Admin@Example.Test", AdminPassword = "correct-horse-battery" });
        for (var i = 0; i < 2; i++)
        {
            await using var db = testDb.NewContext();
            await new DbSeeder(db, new PasswordHasher<User>(), seed, NullLogger<DbSeeder>.Instance).RunAsync(default);
        }

        await using var check = testDb.NewContext();
        var admin = await check.Users.SingleAsync(u => u.Role == Roles.PlatformAdmin);
        Assert.Equal("admin@example.test", admin.Email);
        Assert.Null(admin.TenantId);
        Assert.NotEqual("correct-horse-battery", admin.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(admin, admin.PasswordHash, "correct-horse-battery"));
        Assert.Equal(1, await check.Plans.CountAsync(p => p.Name == DbSeeder.DefaultPlanName));
    }

    [Fact]
    public async Task Database_rejects_inconsistent_user_role_and_tenant()
    {
        var tenant = await testDb.NewTenantAsync();
        await using var db = testDb.NewContext();

        db.Users.Add(new User { Email = "a@x.test", DisplayName = "a", PasswordHash = "h", Role = Roles.PlatformAdmin, TenantId = tenant.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.Users.Add(new User { Email = "b@x.test", DisplayName = "b", PasswordHash = "h", Role = Roles.Owner });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.Users.Add(new User { Email = "c@x.test", DisplayName = "c", PasswordHash = "h", Role = "root", TenantId = tenant.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
