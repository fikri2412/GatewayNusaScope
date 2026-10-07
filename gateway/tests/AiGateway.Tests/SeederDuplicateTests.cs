using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

/// <summary>
/// DbSeeder di database bersih (kelas sendiri, database sendiri): baris yang sudah ada tidak boleh membuat
/// startup gagal. Skenario yang dipakai sama dengan dua proses start berbarengan — pre-check <c>AnyAsync</c>
/// lolos, lalu INSERT menabrak indeks unik.
/// </summary>
public class SeederDuplicateTests(TestDb testDb) : IClassFixture<TestDb>
{
    [Fact]
    public async Task Seeder_does_not_fail_when_the_admin_email_is_already_used()
    {
        // Email Seed:AdminEmail sudah dipakai user tenant dan belum ada platform admin, jadi seeder
        // benar-benar mencapai INSERT users (bukan keluar lewat pre-check).
        var tenant = await testDb.NewTenantAsync();
        await using (var db = testDb.NewContext())
        {
            db.Users.Add(new User
            {
                Email = "bentrok@example.test", DisplayName = "Tenant Owner", PasswordHash = "h",
                Role = Roles.Owner, TenantId = tenant.Id,
            });
            await db.SaveChangesAsync();
        }

        var seed = Options.Create(new SeedOptions { AdminEmail = "Bentrok@Example.Test", AdminPassword = "correct-horse-battery" });

        // Dulu RunAsync melempar DbUpdateException (startup gateway gagal); sekarang baris itu dilewati.
        await using (var db = testDb.NewContext())
            await new DbSeeder(db, new PasswordHasher<User>(), seed, NullLogger<DbSeeder>.Instance).RunAsync(default);

        await using var check = testDb.NewContext();
        Assert.Equal(1, await check.Users.CountAsync(u => u.Email == "bentrok@example.test"));
        Assert.Equal(Roles.Owner, (await check.Users.SingleAsync(u => u.Email == "bentrok@example.test")).Role);
        Assert.Equal(0, await check.Users.CountAsync(u => u.Role == Roles.PlatformAdmin));
        // Langkah sebelumnya tetap jalan: kegagalan baris admin tidak membatalkan data awal lain.
        Assert.Equal(1, await check.Plans.CountAsync(p => p.Name == DbSeeder.DefaultPlanName));
    }
}
