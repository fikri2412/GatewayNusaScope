using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

public class TenantIsolationTests(TestDb testDb) : IClassFixture<TestDb>
{
    private static Provider NewProvider(string? name = null) =>
        new() { Name = name ?? $"p-{Guid.NewGuid():N}", BaseUrl = "https://example.test/v1/" };

    [Fact]
    public async Task Queries_fail_closed_without_tenant_and_return_only_own_rows_with_tenant()
    {
        var a = await testDb.NewTenantAsync();
        var b = await testDb.NewTenantAsync();
        await using (var ctxA = testDb.NewContext(a.Id)) { ctxA.Providers.Add(NewProvider("shared")); await ctxA.SaveChangesAsync(); }
        await using (var ctxB = testDb.NewContext(b.Id)) { ctxB.Providers.Add(NewProvider("shared")); await ctxB.SaveChangesAsync(); }

        await using var none = testDb.NewContext();
        Assert.Empty(await none.Providers.ToListAsync());

        await using var ctx = testDb.NewContext(a.Id);
        var rows = await ctx.Providers.ToListAsync();
        Assert.Single(rows);
        Assert.Equal(a.Id, rows[0].TenantId);
    }

    [Fact]
    public async Task New_rows_take_context_tenant_and_without_any_tenant_are_rejected()
    {
        var a = await testDb.NewTenantAsync();
        await using var ctx = testDb.NewContext(a.Id);
        var p = NewProvider();
        ctx.Providers.Add(p);
        await ctx.SaveChangesAsync();
        Assert.Equal(a.Id, p.TenantId);

        await using var noTenant = testDb.NewContext();
        noTenant.Providers.Add(NewProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => noTenant.SaveChangesAsync());
    }

    [Fact]
    public async Task Cross_tenant_write_and_tenant_change_are_rejected()
    {
        var a = await testDb.NewTenantAsync();
        var b = await testDb.NewTenantAsync();

        await using var ctx = testDb.NewContext(a.Id);
        var foreign = NewProvider();
        foreign.TenantId = b.Id;
        ctx.Providers.Add(foreign);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());

        await using var ctx2 = testDb.NewContext(a.Id);
        var own = NewProvider();
        ctx2.Providers.Add(own);
        await ctx2.SaveChangesAsync();
        own.TenantId = b.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx2.SaveChangesAsync());
    }

    [Fact]
    public async Task Soft_deleted_rows_are_hidden_and_their_name_can_be_reused()
    {
        var a = await testDb.NewTenantAsync();
        await using var ctx = testDb.NewContext(a.Id);
        var first = NewProvider("dup");
        ctx.Providers.Add(first);
        await ctx.SaveChangesAsync();

        await using var dupCtx = testDb.NewContext(a.Id);
        dupCtx.Providers.Add(NewProvider("dup"));
        await Assert.ThrowsAsync<DbUpdateException>(() => dupCtx.SaveChangesAsync());

        first.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        Assert.Empty(await testDb.NewContext(a.Id).Providers.ToListAsync());

        await using var reuse = testDb.NewContext(a.Id);
        reuse.Providers.Add(NewProvider("dup"));
        await reuse.SaveChangesAsync();
    }

    [Fact]
    public async Task Only_one_active_credential_per_provider()
    {
        var a = await testDb.NewTenantAsync();
        await using var ctx = testDb.NewContext(a.Id);
        var provider = NewProvider();
        ctx.Providers.Add(provider);
        await ctx.SaveChangesAsync();

        ctx.ProviderCredentials.Add(new ProviderCredential { ProviderId = provider.Id, ApiKeyEncrypted = "x", KeyHint = "1111" });
        await ctx.SaveChangesAsync();

        ctx.ProviderCredentials.Add(new ProviderCredential { ProviderId = provider.Id, ApiKeyEncrypted = "y", KeyHint = "2222" });
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }
}
