using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using AiGateway.Core.Security;
using AiGateway.Core.WebSearch;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

/// <summary>
/// Registry adalah jalur validasi/default bersama admin API dan seeder; test di sini menguji aturan registry
/// dan perilaku seed langsung tanpa host HTTP.
/// </summary>
public sealed class SearchProviderRegistryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private readonly ProviderKeyProtector _protector =
        new(new EphemeralDataProtectionProvider()); // tanpa direktori kunci sementara yang bocor

    public Task InitializeAsync() => _testDb.InitializeAsync();

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private SearchProviderRegistry Registry(GatewayDbContext db) => new(
        db, _protector, new OutboundSecurityPolicy(new OutboundSecurityOptions()), TimeProvider.System,
        [new TavilySearchAdapter(), new ExaSearchAdapter(), new FirecrawlSearchAdapter()]);

    private DbSeeder Seeder(GatewayDbContext db, WebSearchOptions options) => new(
        db, new PasswordHasher<User>(), Options.Create(new SeedOptions()), NullLogger<DbSeeder>.Instance,
        Options.Create(options), Registry(db));

    [Fact]
    public async Task Create_defaults_name_persists_state_and_rejects_duplicate_with_409()
    {
        var tenant = await _testDb.NewTenantAsync();
        await using var db = _testDb.NewContext(tenant.Id);
        var registry = Registry(db);

        var config = await registry.CreateAsync(
            new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "tvly-secret-12345678"), default);

        Assert.Equal("tavily", config.Name); // nama default = tipe
        Assert.Equal("https://api.tavily.com", config.BaseUri); // default dari metadata adapter
        Assert.Equal("5678", config.KeyHint);
        Assert.True(config.IsEnabled);
        Assert.DoesNotContain("tvly-secret-12345678", config.ApiKeyEncrypted);
        Assert.Equal("tvly-secret-12345678", _protector.Unprotect(tenant.Id, config.ApiKeyEncrypted));

        var state = await db.SearchProviderStates.SingleAsync(s => s.ProviderId == config.Id);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
        Assert.Equal(0, state.Generation);

        var duplicate = await Assert.ThrowsAsync<GatewayException>(() => registry.CreateAsync(
            new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "tvly-other-87654321"), default));
        Assert.Equal(409, duplicate.Status);
        Assert.Equal("provider_exists", duplicate.Code);
        Assert.Equal(1, await db.SearchProviderConfigs.CountAsync());
    }

    [Fact]
    public async Task Create_enforces_type_length_numeric_capability_and_json_rules()
    {
        var tenant = await _testDb.NewTenantAsync();
        await using var db = _testDb.NewContext(tenant.Id);
        var registry = Registry(db);

        async Task Bad(SearchProviderSpec spec, string code)
        {
            var ex = await Assert.ThrowsAsync<GatewayException>(() => registry.CreateAsync(spec, default));
            Assert.Equal(400, ex.Status);
            Assert.Equal(code, ex.Code);
        }

        await Bad(new SearchProviderSpec(Type: "brave", ApiKey: "k-123456789012"), "invalid_type");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily), "invalid_api_key");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", Name: new string('n', 101)), "invalid_name");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", BaseUri: "http://api.tavily.com"), "invalid_base_uri");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", BaseUri: "https://10.0.0.5"), "invalid_base_uri");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", Weight: 0), "invalid_weight");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", Weight: 101), "invalid_weight");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", Capabilities: "browse"), "invalid_capabilities");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", MonthlyBudget: -1m), "invalid_budget");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", CostPerSearch: -1m), "invalid_cost");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", ResetPolicy: "weekly"), "invalid_reset_policy");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", ErrorMappingOverride: "not json"), "invalid_error_mapping");
        await Bad(new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "k", ErrorMappingOverride: """{"unknown":1}"""), "invalid_error_mapping");

        Assert.Equal(0, await db.SearchProviderConfigs.CountAsync());
    }

    [Fact]
    public async Task Short_keys_are_masked_without_disclosure()
    {
        var tenant = await _testDb.NewTenantAsync();
        await using var db = _testDb.NewContext(tenant.Id);
        var registry = Registry(db);

        var shortKey = await registry.CreateAsync(
            new SearchProviderSpec(Type: SearchProviderTypes.Exa, Name: "pendek", ApiKey: "short-key"), default);
        Assert.Equal("****", shortKey.KeyHint);

        var longKey = await registry.CreateAsync(
            new SearchProviderSpec(Type: SearchProviderTypes.Exa, Name: "panjang", ApiKey: "123456789012"), default);
        Assert.Equal("9012", longKey.KeyHint);
    }

    [Fact]
    public async Task Deleted_seed_provider_is_not_resurrected_on_restart()
    {
        var tenant = await _testDb.NewTenantAsync();
        await using (var db = _testDb.NewContext(tenant.Id))
        {
            var config = await Registry(db).CreateAsync(
                new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "tvly-seed-only-123456"), default);
            config.DeletedAt = DateTime.UtcNow;
            config.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        await using (var db = _testDb.NewContext())
            await Seeder(db, new WebSearchOptions
            {
                SeedTenantSlug = tenant.Slug,
                Providers = [new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "tvly-replacement-123456")],
            }).RunAsync(default);
        await using var verify = _testDb.NewContext(tenant.Id);
        Assert.Empty(await verify.SearchProviderConfigs.ToListAsync());
        var deleted = await verify.SearchProviderConfigs.IgnoreQueryFilters()
            .SingleAsync(p => p.TenantId == tenant.Id);
        Assert.NotNull(deleted.DeletedAt);
        Assert.False(deleted.IsEnabled);
    }

    [Fact]
    public async Task Seed_targets_explicit_tenant_and_never_overwrites_existing_registry()
    {
        var tenantA = await _testDb.NewTenantAsync();
        var tenantB = await _testDb.NewTenantAsync();

        await using (var db = _testDb.NewContext())
        {
            await Seeder(db, new WebSearchOptions
            {
                SeedTenantSlug = tenantB.Slug,
                Providers = [new SearchProviderSpec(Type: SearchProviderTypes.Tavily, Name: "Seed B", ApiKey: "tvly-seed-12345678")],
            }).RunAsync(default);
        }

        await using (var verify = _testDb.NewContext(tenantA.Id))
            Assert.Equal(0, await verify.SearchProviderConfigs.CountAsync());

        await using (var verify = _testDb.NewContext(tenantB.Id))
        {
            var config = await verify.SearchProviderConfigs.SingleAsync();
            Assert.Equal("Seed B", config.Name);
            Assert.Equal("5678", config.KeyHint);
            Assert.NotNull(await verify.SearchProviderStates.SingleOrDefaultAsync(s => s.ProviderId == config.Id));
        }

        // Jalankan ulang dengan isi berbeda: registry yang sudah ada tidak ditimpa.
        await using (var db = _testDb.NewContext())
        {
            await Seeder(db, new WebSearchOptions
            {
                SeedTenantSlug = tenantB.Slug,
                Providers = [new SearchProviderSpec(Type: SearchProviderTypes.Exa, Name: "Overwrite", ApiKey: "exa-new-87654321")],
            }).RunAsync(default);
        }

        await using (var verify = _testDb.NewContext(tenantB.Id))
        {
            var config = await verify.SearchProviderConfigs.SingleAsync();
            Assert.Equal("Seed B", config.Name);
            Assert.Equal(SearchProviderTypes.Tavily, config.Type);
            Assert.Equal("5678", config.KeyHint);
        }

        // Target tenant lain: tenant A dapat providenya sendiri, tenant B tidak tersentuh.
        await using (var db = _testDb.NewContext())
        {
            await Seeder(db, new WebSearchOptions
            {
                SeedTenantSlug = tenantA.Slug,
                Providers = [new SearchProviderSpec(Type: SearchProviderTypes.Exa, Name: "Seed A", ApiKey: "exa-seed-87654321")],
            }).RunAsync(default);
        }

        await using (var verify = _testDb.NewContext(tenantA.Id))
        {
            var config = await verify.SearchProviderConfigs.SingleAsync();
            Assert.Equal("Seed A", config.Name);
            Assert.Equal(SearchProviderTypes.Exa, config.Type);
        }

        await using (var verifyB = _testDb.NewContext(tenantB.Id))
        {
            var config = await verifyB.SearchProviderConfigs.SingleAsync();
            Assert.Equal("Seed B", config.Name);
        }
    }

    [Fact]
    public async Task Seed_rolls_back_all_providers_when_one_is_malformed()
    {
        var tenant = await _testDb.NewTenantAsync();
        await using var db = _testDb.NewContext();
        var seeder = Seeder(db, new WebSearchOptions
        {
            SeedTenantSlug = tenant.Slug,
            Providers =
            [
                new SearchProviderSpec(Type: SearchProviderTypes.Tavily, Name: "Valid", ApiKey: "tvly-seed-12345678"),
                new SearchProviderSpec(Type: "brave", Name: "Rusak", ApiKey: "k-123456789012"),
            ],
        });

        var ex = await Assert.ThrowsAsync<GatewayException>(() => seeder.RunAsync(default));
        Assert.Equal("invalid_type", ex.Code);
        Assert.Equal(GatewayDbContext.NoTenant, db.CurrentTenantId); // konteks tenant dipulihkan di finally

        await using var verify = _testDb.NewContext(tenant.Id);
        Assert.Equal(0, await verify.SearchProviderConfigs.CountAsync());
        Assert.Equal(0, await verify.SearchProviderStates.CountAsync());
    }

    [Fact]
    public async Task Seed_with_providers_requires_explicit_tenant_slug()
    {
        var tenant = await _testDb.NewTenantAsync();
        await using var db = _testDb.NewContext();
        var seeder = Seeder(db, new WebSearchOptions
        {
            Providers = [new SearchProviderSpec(Type: SearchProviderTypes.Tavily, ApiKey: "tvly-seed-12345678")],
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.RunAsync(default));

        await using var verify = _testDb.NewContext(tenant.Id);
        Assert.Equal(0, await verify.SearchProviderConfigs.CountAsync());
    }
}
