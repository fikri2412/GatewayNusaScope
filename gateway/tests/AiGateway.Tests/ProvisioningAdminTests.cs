using System.Net;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class ProvisioningAdminTests(TestDb db) : GatewayTestBase(db)
{
    private Task<T> InTenantAsync<T>(Scenario s, Func<ProvisioningService, GatewayDbContext, Task<T>> action) => WithTenantAsync(s.TenantId, action);

    private static async Task<string> CodeOf(Func<Task> action) => (await Assert.ThrowsAsync<GatewayException>(action)).Code;

    [Fact]
    public async Task Provider_updates_are_validated_and_names_stay_unique()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, _) =>
        {
            var other = await prov.CreateCustomProviderAsync("second", "https://two.test/v1", "key-for-second-0001", null, "Token ", "models", default);

            Assert.Equal("already_exists", await CodeOf(() => prov.UpdateProviderAsync(other.Id, new ProviderPatch(Name: "up"), default)));
            Assert.Equal("invalid_base_url", await CodeOf(() => prov.UpdateProviderAsync(other.Id, new ProviderPatch(BaseUrl: "ftp://x"), default)));
            Assert.Equal("invalid_auth_header", await CodeOf(() => prov.UpdateProviderAsync(other.Id, new ProviderPatch(AuthHeader: "bad header"), default)));
            Assert.Equal("invalid_models_path", await CodeOf(() => prov.UpdateProviderAsync(other.Id, new ProviderPatch(ModelsPath: "../etc"), default)));

            var changed = await prov.UpdateProviderAsync(other.Id,
                new ProviderPatch(Name: "renamed", BaseUrl: "https://three.test/v1", Enabled: false, AuthHeader: "x-key", ClearModelsPath: true), default);
            Assert.Equal(("renamed", "https://three.test/v1", false, "x-key", "Token ", null),
                (changed.Name, changed.BaseUrl, changed.Enabled, changed.AuthHeader, changed.AuthPrefix, changed.ModelsPath));
            return 0;
        });
    }

    [Fact]
    public async Task A_provider_in_use_cannot_be_deleted_and_a_deleted_one_frees_its_name_and_key()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, ctx) =>
        {
            Assert.Equal("in_use", await CodeOf(() => prov.DeleteProviderAsync(s.ProviderId, default)));

            var spare = await prov.CreateCustomProviderAsync("spare", "https://spare.test/v1", "key-for-spare-000001", null, null, null, default);
            await prov.ReplaceRoutesAsync(s.ModelId, [new RouteSpec(spare.Id, "real-model")], default); // lepas provider asli dari model
            await prov.DeleteProviderAsync(s.ProviderId, default);

            Assert.False(await ctx.Providers.AnyAsync(p => p.Id == s.ProviderId)); // tersaring (soft delete)
            Assert.Equal(0, await ctx.ProviderCredentials.CountAsync(c => c.ProviderId == s.ProviderId && c.Status == CredentialStatuses.Active));
            await prov.CreateCustomProviderAsync("up", "https://again.test/v1", "key-for-again-000001", null, null, null, default); // nama bisa dipakai lagi
            return 0;
        });
    }

    [Fact]
    public async Task Replacing_routes_changes_where_traffic_goes_and_rejects_foreign_providers()
    {
        var s = await SetupAsync();
        var other = await SetupAsync();
        var spareId = await InTenantAsync(s, async (prov, _) =>
        {
            var spare = await prov.CreateCustomProviderAsync("spare", "https://spare.test/v1", "key-for-spare-000002", null, null, null, default);
            Assert.Equal("no_routes", await CodeOf(() => prov.ReplaceRoutesAsync(s.ModelId, [], default)));
            Assert.Equal("not_found", await CodeOf(() => prov.ReplaceRoutesAsync(s.ModelId, [new RouteSpec(other.ProviderId, "x")], default)));
            Assert.Equal("invalid_route", await CodeOf(() => prov.ReplaceRoutesAsync(s.ModelId, [new RouteSpec(spare.Id, "x", Weight: 0)], default)));
            Assert.Equal("not_found", await CodeOf(() => prov.ReplaceRoutesAsync(other.ModelId, [new RouteSpec(spare.Id, "x")], default)));
            await prov.ReplaceRoutesAsync(s.ModelId, [new RouteSpec(spare.Id, "spare-model")], default);
            return spare.Id;
        });

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("spare.test", Upstream.Requests.Single().Uri.Host);
        Assert.Contains("spare-model", Upstream.Requests.Single().Body);
        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Equal(spareId, Assert.Single(await ctx.ModelRoutes.Where(r => r.ModelId == s.ModelId).ToListAsync()).ProviderId);
    }

    [Fact]
    public async Task A_new_price_set_applies_from_now_keeps_history_and_supports_tiers()
    {
        var s = await SetupAsync(); // harga awal 1 / 2
        var beforeNew = DateTime.MinValue;
        await InTenantAsync(s, async (prov, ctx) =>
        {
            Assert.Equal("invalid_price", await CodeOf(() => prov.AddPriceSetAsync(s.ModelId, new PriceSet(-1, 1), default)));
            Assert.Equal("invalid_price", await CodeOf(() => prov.AddPriceSetAsync(s.ModelId, new PriceSet(1, 1, Tiers: [new PriceTier(0, 1, 1, null, null)]), default)));
            Assert.Equal("invalid_price", await CodeOf(() => prov.AddPriceSetAsync(s.ModelId, new PriceSet(1, 1, Tiers: [new PriceTier(5, 1, 1, null, null), new PriceTier(5, 2, 2, null, null)]), default)));
            Assert.Equal("invalid_currency", await CodeOf(() => prov.AddPriceSetAsync(s.ModelId, new PriceSet(1, 1, Currency: "usd"), default)));

            await Task.Delay(20); // pastikan effective_from lebih baru dari harga awal
            beforeNew = DateTime.UtcNow;
            await Task.Delay(20);
            await prov.AddPriceSetAsync(s.ModelId, new PriceSet(3, 6, CacheRead: 0.3m, Tiers: [new PriceTier(1_000, 5, 9, 0.5m, null)]), default);

            var all = await ctx.ModelPrices.Where(p => p.ModelId == s.ModelId).ToListAsync();
            Assert.Equal(3, all.Count); // harga awal (1 baris) + harga baru (dasar + 1 tier)
            return 0;
        });
        await using var check = Db.NewContext(s.TenantId);
        var prices = await check.ModelPrices.Where(p => p.ModelId == s.ModelId).ToListAsync();
        Assert.Equal(3m, Pricing.Pick(prices, DateTime.UtcNow.AddSeconds(1), 10)!.InputPricePer1M);
        Assert.Equal(5m, Pricing.Pick(prices, DateTime.UtcNow.AddSeconds(1), 5_000)!.InputPricePer1M);
        Assert.Equal(1m, Pricing.Pick(prices, beforeNew, 10)!.InputPricePer1M); // harga lama tetap utuh untuk waktu sebelum perubahan
    }

    [Fact]
    public async Task Model_updates_and_soft_delete_affect_traffic_and_free_the_alias()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, ctx) =>
        {
            Assert.Equal("invalid_max_output", await CodeOf(() => prov.UpdateModelAsync(s.ModelId, new ModelPatch(MaxOutputTokens: 0), default)));
            Assert.Equal("invalid_description", await CodeOf(() => prov.UpdateModelAsync(s.ModelId, new ModelPatch(Description: new string('x', 501)), default)));

            var m = await prov.UpdateModelAsync(s.ModelId, new ModelPatch(Description: "hello", MaxOutputTokens: 77), default);
            Assert.Equal(("hello", 77), (m.Description, m.MaxOutputTokens));
            m = await prov.UpdateModelAsync(s.ModelId, new ModelPatch(Description: "", ClearMaxOutputTokens: true), default);
            Assert.Equal((null, null), (m.Description, m.MaxOutputTokens));

            await prov.DeleteModelAsync(s.ModelId, default);
            Assert.False(await ctx.Models.AnyAsync(x => x.Id == s.ModelId));
            await prov.CreateModelAsync(new ModelSpec("gpt-test", "x"), [new RouteSpec(s.ProviderId, "x")], default); // alias bebas lagi
            return 0;
        });
    }

    [Fact]
    public async Task Suspending_a_project_blocks_traffic_and_deleting_it_revokes_its_keys()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, _) =>
        {
            Assert.Equal("invalid_status", await CodeOf(() => prov.UpdateProjectAsync(s.ProjectId, new ProjectPatch(Status: "paused"), default)));
            Assert.Equal("invalid_retention", await CodeOf(() => prov.UpdateProjectAsync(s.ProjectId, new ProjectPatch(ContentRetentionDays: 0), default)));

            var p = await prov.UpdateProjectAsync(s.ProjectId, new ProjectPatch(Status: ProjectStatuses.Suspended, LogContent: true, ContentRetentionDays: 30), default);
            Assert.Equal((ProjectStatuses.Suspended, true, 30), (p.Status, p.LogContent, p.ContentRetentionDays));
            return 0;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await PostChatAsync(s.ApiKey, Chat())).StatusCode);

        await InTenantAsync(s, async (prov, _) =>
        {
            await prov.UpdateProjectAsync(s.ProjectId, new ProjectPatch(Status: ProjectStatuses.Active, ClearRetention: true), default);
            return 0;
        });
        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(s.ApiKey, Chat())).StatusCode);

        await InTenantAsync(s, async (prov, _) => { await prov.DeleteProjectAsync(s.ProjectId, default); return 0; });
        var denied = await PostChatAsync(s.ApiKey, Chat());
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("api_key_revoked", ErrorCode(await BodyAsync(denied)));
    }

    [Fact]
    public async Task Api_keys_can_be_revoked_edited_and_validated()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, ctx) =>
        {
            Assert.Equal("invalid_ip", await CodeOf(() => prov.UpdateApiKeyAsync(s.KeyId, new ApiKeyPatch(AllowedIps: ["300.0.0.1"]), default)));
            Assert.Equal("invalid_expiry", await CodeOf(() => prov.UpdateApiKeyAsync(s.KeyId, new ApiKeyPatch(ExpiresAt: DateTime.UtcNow.AddDays(-1)), default)));

            var k = await prov.UpdateApiKeyAsync(s.KeyId, new ApiKeyPatch(Name: "renamed", AllowedIps: ["10.0.0.0/8"], ExpiresAt: DateTime.UtcNow.AddDays(1)), default);
            Assert.Equal(("renamed", """["10.0.0.0/8"]"""), (k.Name, k.AllowedIpsJson));
            k = await prov.UpdateApiKeyAsync(s.KeyId, new ApiKeyPatch(AllowedIps: [], ClearExpiry: true), default);
            Assert.Equal((null, null), (k.AllowedIpsJson, k.ExpiresAt));

            await prov.RevokeApiKeyAsync(s.KeyId, default);
            var revokedAt = (await ctx.ApiKeys.AsNoTracking().SingleAsync(x => x.Id == s.KeyId)).RevokedAt;
            Assert.NotNull(revokedAt);
            await prov.RevokeApiKeyAsync(s.KeyId, default); // idempoten: waktu pencabutan tidak bergeser
            Assert.Equal(revokedAt, (await ctx.ApiKeys.AsNoTracking().SingleAsync(x => x.Id == s.KeyId)).RevokedAt);
            return 0;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostChatAsync(s.ApiKey, Chat())).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_policy_lifts_its_limits_and_unknown_policies_are_not_found()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, _) =>
        {
            await prov.SetPolicyAsync(PolicyScopes.Key, s.KeyId, new PolicySpec(AllowedModels: ["nothing-matches"]), default);
            return 0;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await PostChatAsync(s.ApiKey, Chat())).StatusCode);

        await InTenantAsync(s, async (prov, _) =>
        {
            await prov.DeletePolicyAsync(PolicyScopes.Key, s.KeyId, default);
            Assert.Equal("not_found", await CodeOf(() => prov.DeletePolicyAsync(PolicyScopes.Key, s.KeyId, default)));
            return 0;
        });
        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(s.ApiKey, Chat())).StatusCode);
    }

    [Fact]
    public async Task A_stale_configuration_write_reports_a_conflict_instead_of_failing()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, db) =>
        {
            var policy = await prov.SetPolicyAsync(PolicyScopes.Key, s.KeyId, new PolicySpec(MaxTokensPerRequest: 10), default);
            // row_version naik di database tanpa entity yang dilacak di sini tahu (mis. permintaan admin lain).
            await db.Database.ExecuteSqlRawAsync("UPDATE policies SET max_tokens_per_request = 20 WHERE id = {0}", policy.Id);

            var ex = await Assert.ThrowsAsync<GatewayException>(() =>
                prov.SetPolicyAsync(PolicyScopes.Key, s.KeyId, new PolicySpec(MaxTokensPerRequest: 30), default));

            Assert.Equal((409, "concurrent_update"), (ex.Status, ex.Code));
            return 0;
        });
    }

    [Fact]
    public async Task Importing_models_validates_every_spec_before_writing_anything()
    {
        var s = await SetupAsync();
        await InTenantAsync(s, async (prov, _) =>
        {
            Assert.Equal("invalid_alias", await CodeOf(() => prov.ImportModelsAsync(s.ProviderId,
                [new ModelSpec("fine-model", "fine-model"), new ModelSpec("bad model", "bad-model")], default)));
            return 0;
        });

        await using var check = Db.NewContext(s.TenantId);
        Assert.Empty(await check.Models.Where(m => m.Alias == "fine-model").ToListAsync()); // spec pertama tidak ikut tersimpan
        Assert.Empty(await check.ModelRoutes.Where(r => r.UpstreamModel == "fine-model").ToListAsync());
    }

    [Fact]
    public async Task Concurrent_project_creation_cannot_exceed_the_plan_limit()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var plan = await scope.ServiceProvider.GetRequiredService<PlatformService>()
            .CreatePlanAsync(new PlanSpec($"race-{Guid.NewGuid():N}", MaxProjects: 1, MaxApiKeys: null, MaxRequestsPerMonth: null, MaxTokensPerMonth: null), default);
        var tenant = await scope.ServiceProvider.GetRequiredService<ProvisioningService>()
            .CreateTenantAsync("Race", Guid.NewGuid().ToString("N"), plan.Id, default);

        async Task<string> TryCreateAsync(string name)
        {
            await using var own = Factory.Services.CreateAsyncScope();
            var db = own.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.CurrentTenantId = tenant.Id;
            try
            {
                await own.ServiceProvider.GetRequiredService<ProvisioningService>().CreateProjectAsync(name, default);
                return "created";
            }
            catch (GatewayException ex) { return ex.Code; }
        }

        var results = await Task.WhenAll(TryCreateAsync("race-a"), TryCreateAsync("race-b"));

        Assert.Equal(1, results.Count(r => r == "created"));
        Assert.Equal("plan_limit_reached", Assert.Single(results, r => r != "created"));
        await using var check = Db.NewContext(tenant.Id);
        Assert.Single(await check.Projects.ToListAsync());
    }
}
