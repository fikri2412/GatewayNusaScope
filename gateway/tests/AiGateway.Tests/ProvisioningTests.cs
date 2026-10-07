using System.Net;
using System.Text.Json;
using AiGateway.Core.Catalog;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class ProvisioningTests(TestDb db) : GatewayTestBase(db)
{
    private async Task<long> NewTenantIdAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ProvisioningService>()
            .CreateTenantAsync("T", Guid.NewGuid().ToString("N"), null, default)).Id;
    }

    private static async Task<GatewayException> CodeOf(Func<Task> action) => await Assert.ThrowsAsync<GatewayException>(action);

    [Fact]
    public async Task Importing_catalog_models_creates_routes_and_tiered_prices_and_skips_unsupported_ones()
    {
        long templateId;
        await using (var ctx = Db.NewContext())
        {
            templateId = (await ctx.ProviderTemplates.SingleAsync(t => t.Code == DbSeeder.OpenCodeTemplateCode)).Id;
            ctx.CatalogModels.AddRange(
                new CatalogModel
                {
                    TemplateId = templateId, UpstreamModel = "priced-model", DisplayName = "Priced",
                    InputPricePer1M = 2m, OutputPricePer1M = 4m, CacheReadPricePer1M = 0.2m, MaxOutputTokens = 8000,
                    ExtraTiersJson = JsonSerializer.Serialize(new[] { new PriceTier(200_001, 4m, 6m, 0.4m, null) }),
                },
                new CatalogModel { TemplateId = templateId, UpstreamModel = "claude-like", DisplayName = "Claude", ApiFamily = ApiFamilies.AnthropicMessages },
                new CatalogModel { TemplateId = templateId, UpstreamModel = "switched-off", DisplayName = "Off", Enabled = false });
            await ctx.SaveChangesAsync();
        }
        var tenantId = await NewTenantIdAsync();

        await WithTenantAsync(tenantId, async (prov, db) =>
        {
            var provider = await prov.CreateProviderFromTemplateAsync(DbSeeder.OpenCodeTemplateCode, "oc", "oc_sk_testkey_0000", null, default);
            Assert.Equal("https://opencode.ai/inference/openai/v1/", provider.BaseUrl);

            var specs = await prov.CatalogSpecsAsync(provider.Id, null, default);
            var offered = specs.Select(x => x.Alias).ToArray();
            Assert.Contains("priced-model", offered);
            Assert.DoesNotContain("claude-like", offered); // keluarga API belum didukung gateway
            Assert.DoesNotContain("switched-off", offered);

            var created = await prov.ImportModelsAsync(provider.Id, specs, default);
            Assert.Equal(specs.Count, created.Count);
            Assert.Empty(await prov.ImportModelsAsync(provider.Id, specs, default)); // idempoten

            var priced = await db.Models.SingleAsync(m => m.Alias == "priced-model");
            Assert.Equal(8000, priced.MaxOutputTokens);
            Assert.Equal("priced-model", (await db.ModelRoutes.SingleAsync(r => r.ModelId == priced.Id)).UpstreamModel);
            var prices = await db.ModelPrices.Where(p => p.ModelId == priced.Id).ToListAsync();
            Assert.Equal([0, 200_001], prices.Select(p => p.MinInputTokens).OrderBy(x => x).ToArray());
            Assert.Equal(2m, Pricing.Pick(prices, DateTime.UtcNow.AddMinutes(1), 1_000)!.InputPricePer1M);
            Assert.Equal(4m, Pricing.Pick(prices, DateTime.UtcNow.AddMinutes(1), 300_000)!.InputPricePer1M);
            return 0;
        });
    }

    [Fact]
    public async Task Discovery_lists_provider_models_with_the_configured_auth_header()
    {
        var tenantId = await NewTenantIdAsync();
        await WithTenantAsync(tenantId, async (prov, _) =>
        {
            var provider = await prov.CreateCustomProviderAsync("cc", "https://cc.test/v1", "key-for-custom-1", "x-api-key", "", "models", default);
            Upstream.Respond = _ => FakeUpstream.Json(200, """{"data":[{"id":"a"},{"id":"b"},{"id":"a"},{"nope":1}]}""");

            Assert.Equal(["a", "b"], await prov.DiscoverModelsAsync(provider.Id, default));
            var request = Upstream.Requests.Single();
            Assert.Equal("https://cc.test/v1/models", request.Uri.ToString());
            Assert.Equal("key-for-custom-1", request.Headers["x-api-key"]);
            Assert.False(request.Headers.ContainsKey("authorization"));

            Upstream.Respond = _ => FakeUpstream.Json(500, "{}");
            Assert.Equal("discovery_failed", (await CodeOf(() => prov.DiscoverModelsAsync(provider.Id, default))).Code);

            var noDiscovery = await prov.CreateCustomProviderAsync("plain", "https://plain.test/v1", "key-for-custom-2", null, null, null, default);
            Assert.Equal("discovery_unsupported", (await CodeOf(() => prov.DiscoverModelsAsync(noDiscovery.Id, default))).Code);
            return 0;
        });
    }

    [Fact]
    public async Task Provisioning_validates_input_and_reports_conflicts()
    {
        var tenantId = await NewTenantIdAsync();
        await WithTenantAsync(tenantId, async (prov, _) =>
        {
            foreach (var url in new[] { "ftp://x.test/v1", "https://user:pw@x.test/v1", "not a url", "https://x.test/v1?k=v" })
                Assert.Equal("invalid_base_url", (await CodeOf(() => prov.CreateCustomProviderAsync("p-" + Guid.NewGuid(), url, "k-0123456789", null, null, null, default))).Code);
            Assert.Equal("invalid_api_key", (await CodeOf(() => prov.CreateCustomProviderAsync("p2", "https://x.test/v1", "bad\r\nkey", null, null, null, default))).Code);
            Assert.Equal("invalid_auth_header", (await CodeOf(() => prov.CreateCustomProviderAsync("p3", "https://x.test/v1", "k-0123456789", "bad header", null, null, default))).Code);

            var provider = await prov.CreateCustomProviderAsync("dup", "https://x.test/v1", "k-0123456789", null, null, null, default);
            Assert.Equal("already_exists", (await CodeOf(() => prov.CreateCustomProviderAsync("dup", "https://x.test/v1", "k-0123456789", null, null, null, default))).Code);

            Assert.Equal("invalid_alias", (await CodeOf(() => prov.CreateModelAsync(new ModelSpec("has space", "m"), [new RouteSpec(provider.Id, "m")], default))).Code);
            Assert.Equal("no_routes", (await CodeOf(() => prov.CreateModelAsync(new ModelSpec("ok", "m"), [], default))).Code);
            Assert.Equal("not_found", (await CodeOf(() => prov.CreateModelAsync(new ModelSpec("ok", "m"), [new RouteSpec(provider.Id + 999, "m")], default))).Code);

            var project = await prov.CreateProjectAsync("proj", default);
            Assert.Equal("already_exists", (await CodeOf(() => prov.CreateProjectAsync("proj", default))).Code);
            Assert.Equal("invalid_ip", (await CodeOf(() => prov.CreateApiKeyAsync(project.Id, "k", null, ["999.1.1.1"], null, default))).Code);
            Assert.Equal("invalid_expiry", (await CodeOf(() => prov.CreateApiKeyAsync(project.Id, "k", DateTime.UtcNow.AddDays(-1), null, null, default))).Code);
            Assert.Equal("invalid_slug", (await CodeOf(() => prov.CreateTenantAsync("T", "Bad Slug", null, default))).Code);
            return 0;
        });
    }

    [Fact]
    public async Task A_tenant_cannot_use_another_tenants_provider_or_project()
    {
        var a = await SetupAsync();
        var otherTenantId = await NewTenantIdAsync();

        await WithTenantAsync(otherTenantId, async (prov, _) =>
        {
            Assert.Equal("not_found", (await CodeOf(() => prov.RotateProviderKeyAsync(a.ProviderId, "sk-steal-12345678", default))).Code);
            Assert.Equal("not_found", (await CodeOf(() => prov.CreateModelAsync(new ModelSpec("x", "m"), [new RouteSpec(a.ProviderId, "m")], default))).Code);
            Assert.Equal("not_found", (await CodeOf(() => prov.CreateApiKeyAsync(a.ProjectId, "k", null, null, null, default))).Code);
            Assert.Equal("not_found", (await CodeOf(() => prov.DiscoverModelsAsync(a.ProviderId, default))).Code);
            return 0;
        });
    }

    private const string ConfigFixture = """
        {"providers":{"fakeprov":{"name":"Fake Provider","package":"aisdk:@ai-sdk/openai-compatible",
          "settings":{"baseURL":"https://fake.test/v1"},"models":{
          "m-chat":{"name":"Chat Model","capabilities":{"tools":true,"input":["text","image"],"output":["text"]},
                    "cost":[{"input":0.3,"output":1.2,"cache":{"read":0.006}}],"limit":{"context":1000000,"output":384000}},
          "m-off":{"name":"Off","disabled":true,"capabilities":{"tools":false,"input":["text"],"output":["text"]},
                   "cost":[{"input":0,"output":0}],"limit":{"context":1000}},
          "m-claude":{"name":"Claude","package":"aisdk:@ai-sdk/anthropic","capabilities":{"tools":true,"input":["text"],"output":["text"]},
                      "cost":[{"input":3,"output":15,"cache":{"read":0.3,"write":3.75}},
                              {"tier":{"type":"context","size":200000},"input":6,"output":22.5,"cache":{"read":0.6,"write":7.5}}],
                      "limit":{"context":1000000,"output":64000}},
          "m-unknown":{"name":"Unknown family","package":"aisdk:@something/else","cost":[{"input":1,"output":1}]}}}}}
        """;

    [Fact]
    public void Config_parser_maps_families_tiers_capabilities_and_skips_unknown_families()
    {
        var provider = Assert.Single(OpenCodeCatalogSync.Parse(System.Text.Json.Nodes.JsonNode.Parse(ConfigFixture)!));
        Assert.Equal(("fakeprov", "https://fake.test/v1"), (provider.Key, provider.BaseUrl));
        Assert.Equal(["m-chat", "m-off", "m-claude"], provider.Models.Select(m => m.Id).ToArray());

        var chat = provider.Models[0];
        Assert.Equal((ApiFamilies.OpenAiChat, false, true, true), (chat.ApiFamily, chat.Disabled, chat.Tools, chat.Vision));
        Assert.Equal((0.3m, 1.2m, 0.006m, 1_000_000, 384_000), (chat.Input, chat.Output, chat.CacheRead, chat.Context, chat.MaxOutput));
        Assert.True(provider.Models[1].Disabled);

        var claude = provider.Models[2];
        Assert.Equal(ApiFamilies.AnthropicMessages, claude.ApiFamily);
        var tier = Assert.Single(claude.ExtraTiers);
        Assert.Equal(new PriceTier(200_001, 6m, 22.5m, 0.6m, 7.5m), tier); // "> 200K" jadi >= 200001
    }

    [Fact]
    public async Task Catalog_sync_upserts_models_and_never_overwrites_manual_rows()
    {
        const string url = "https://catalog.test/api/v2/config";
        Upstream.Respond = _ => FakeUpstream.Json(200, ConfigFixture);

        await using var scope = Factory.Services.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<OpenCodeCatalogSync>();
        var first = await sync.SyncAsync("oc_sk_sync_key", url, default);

        Assert.Equal("Bearer oc_sk_sync_key", Upstream.Requests.Single().Headers["authorization"]);
        Assert.Equal((1, 3, 0, 0, 1), (first.TemplatesAdded, first.ModelsAdded, first.ModelsUpdated, first.ModelsSkippedManual, first.ModelsUnsupported));

        await using var ctx = Db.NewContext();
        var template = await ctx.ProviderTemplates.SingleAsync(t => t.Code == "fakeprov");
        Assert.Equal(("https://fake.test/v1/", SyncKinds.OpenCodeConfig, url), (template.DefaultBaseUrl, template.SyncKind, template.SyncUrl));
        var off = await ctx.CatalogModels.SingleAsync(m => m.TemplateId == template.Id && m.UpstreamModel == "m-off");
        Assert.False(off.Enabled);

        var manual = await ctx.CatalogModels.SingleAsync(m => m.TemplateId == template.Id && m.UpstreamModel == "m-chat");
        manual.Source = CatalogSources.Manual;
        manual.InputPricePer1M = 99m;
        await ctx.SaveChangesAsync();

        await using var scope2 = Factory.Services.CreateAsyncScope(); // satu scope per sinkronisasi, seperti satu request
        var second = await scope2.ServiceProvider.GetRequiredService<OpenCodeCatalogSync>().SyncAsync("oc_sk_sync_key", url, default);
        Assert.Equal((0, 0, 2, 1, 1), (second.TemplatesAdded, second.ModelsAdded, second.ModelsUpdated, second.ModelsSkippedManual, second.ModelsUnsupported));

        await using var check = Db.NewContext();
        Assert.Equal(99m, (await check.CatalogModels.SingleAsync(m => m.Id == manual.Id)).InputPricePer1M);
    }

    [Fact]
    public async Task Catalog_sync_failures_are_reported_without_leaking_the_key()
    {
        Upstream.Respond = _ => FakeUpstream.Json(401, "{}");
        await using var scope = Factory.Services.CreateAsyncScope();
        var sync = scope.ServiceProvider.GetRequiredService<OpenCodeCatalogSync>();

        var ex = await CodeOf(() => sync.SyncAsync("oc_sk_secret_value", "https://catalog.test/c", default));

        Assert.Equal((502, "catalog_sync_failed"), (ex.Status, ex.Code));
        Assert.DoesNotContain("oc_sk_secret_value", ex.Message);

        Upstream.Respond = _ => FakeUpstream.Json(200, "[1,2");
        Assert.Equal("catalog_sync_failed", (await CodeOf(() => sync.SyncAsync("k", "https://catalog.test/c", default))).Code);
    }
}
