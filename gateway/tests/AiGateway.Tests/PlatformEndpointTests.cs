using System.Net;
using System.Net.Http.Json;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Platform plane lewat HTTP sungguhan: hanya <c>platform_admin</c> yang boleh masuk, katalog master tetap bisa
/// dibaca tenant, alur plan/tenant (owner, suspend, reset), validasi harga/tier katalog manual, dan tidak ada
/// token atau service key yang bocor ke audit maupun daftar.
/// </summary>
public class PlatformEndpointTests(TestDb db) : GatewayTestBase(db)
{
    private const string SeedAdminEmail = "test-admin@example.test";
    private const string SeedAdminPassword = "test-password-123456";

    private const string SyncConfig = """
        {"providers":{"opencode-go":{"name":"OpenCode Go","settings":{"baseURL":"https://api.opencode.test/v1"},
        "package":"aisdk:@ai-sdk/openai-compatible","models":{"glm-5.3-flash":{"name":"GLM 5.3 Flash",
        "limit":{"context":200000,"input":180000,"output":8000},"capabilities":{"tools":true,"input":["text","image"]},
        "cost":[{"input":0.3,"output":1.2}]}}}}}
        """;

    private async Task<string> LoginAsync(string email, string password = StrongPassword)
    {
        var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["accessToken"]!;
    }

    private Task<string> PlatformAdminAsync() => LoginAsync(SeedAdminEmail, SeedAdminPassword);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token) =>
        SendAsync(method, path, token, body: (object?)null);

    private async Task<HttpResponseMessage> SendAsync<T>(HttpMethod method, string path, string? token, T body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    private async Task<Guid> TenantPublicIdAsync(long internalId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Tenants.Where(t => t.Id == internalId).Select(t => t.PublicId).SingleAsync();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return ErrorCode(await BodyAsync(response));
    }

    [Fact]
    public async Task Platform_routes_reject_tenants_while_catalog_reads_stay_open()
    {
        var admin = await PlatformAdminAsync();
        var (tenantId, _, ownerEmail) = await NewUserAsync(Roles.Owner);
        var (_, _, viewerEmail) = await NewUserAsync(Roles.Viewer, tenantId);
        var ownerToken = await LoginAsync(ownerEmail);
        var viewerToken = await LoginAsync(viewerEmail);

        // Tanpa token: 401 (bukan 403) supaya klien tahu harus login.
        foreach (var path in new[] { "/platform/api/plans", "/platform/api/tenants", "/platform/api/usage?from=2026-01-01&to=2026-01-02", "/platform/api/audit", "/admin/api/catalog/templates" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, path, null)).StatusCode);

        // Tenant (owner maupun viewer) tidak punya akses ke platform plane, termasuk overview usage/audit.
        foreach (var token in new[] { ownerToken, viewerToken })
        {
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                     {
                         (HttpMethod.Get, "/platform/api/plans", null),
                         (HttpMethod.Post, "/platform/api/plans", new { name = "nope" }),
                         (HttpMethod.Get, "/platform/api/tenants", null),
                         (HttpMethod.Post, "/platform/api/tenants", new { name = "Nope", slug = "nope", ownerEmail = "nope@example.test", ownerDisplayName = "N" }),
                         (HttpMethod.Get, "/platform/api/usage?from=2026-01-01&to=2026-01-02", null),
                         (HttpMethod.Get, "/platform/api/audit", null),
                         (HttpMethod.Post, "/platform/api/templates", new { code = "nope", name = "Nope", defaultBaseUrl = "https://nope.test/" }),
                         (HttpMethod.Post, "/platform/api/catalog/sync", new { serviceKey = "x" }),
                     })
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(method, path, token, body)).StatusCode);
        }

        // Katalog master adalah master data bersama: tenant viewer dan platform admin sama-sama boleh membaca.
        foreach (var token in new[] { viewerToken, admin })
        {
            var templates = await SendAsync(HttpMethod.Get, "/admin/api/catalog/templates", token);
            Assert.Equal(HttpStatusCode.OK, templates.StatusCode);
            Assert.Contains((await BodyAsync(templates)).AsArray()!, t => (string?)t!["code"] == DbSeeder.OpenCodeTemplateCode);

            var models = await SendAsync(HttpMethod.Get, $"/admin/api/catalog/models?templateCode={DbSeeder.OpenCodeTemplateCode}", token);
            Assert.Equal(HttpStatusCode.OK, models.StatusCode);
            Assert.Contains((await BodyAsync(models)).AsArray()!, m => (string?)m!["upstreamModel"] == DbSeeder.OpenCodeDefaultModel);
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/platform/api/plans", admin)).StatusCode);
    }

    [Fact]
    public async Task Plans_validate_limits_and_cannot_be_deleted_while_a_tenant_uses_them()
    {
        var admin = await PlatformAdminAsync();
        var name = $"plan-{Guid.NewGuid():N}";

        var created = await SendAsync(HttpMethod.Post, "/platform/api/plans", admin,
            new { name, maxProjects = 3, maxApiKeys = 5, maxRequestsPerMonth = 1000, maxTokensPerMonth = 2000 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var plan = await BodyAsync(created);
        var planId = (long)plan["id"]!;
        Assert.Equal(name, (string?)plan["name"]);
        Assert.Equal(3, (int?)plan["maxProjects"]);
        Assert.Equal(5, (int?)plan["maxApiKeys"]);
        Assert.Equal(1000, (long?)plan["maxRequestsPerMonth"]);
        Assert.Equal(2000, (long?)plan["maxTokensPerMonth"]);

        // Nama ganda, nama kosong, dan batas <= 0 ditolak.
        Assert.Equal("already_exists", await CodeAsync(
            await SendAsync(HttpMethod.Post, "/platform/api/plans", admin, new { name }), HttpStatusCode.Conflict));
        Assert.Equal("invalid_name", await CodeAsync(
            await SendAsync(HttpMethod.Post, "/platform/api/plans", admin, new { name = "   " }), HttpStatusCode.BadRequest));
        foreach (var bad in new object[]
                 {
                     new { name = $"x-{Guid.NewGuid():N}", maxProjects = 0 },
                     new { name = $"x-{Guid.NewGuid():N}", maxApiKeys = -2 },
                     new { name = $"x-{Guid.NewGuid():N}", maxTokensPerMonth = -1L },
                 })
            Assert.Equal("invalid_limit", await CodeAsync(
                await SendAsync(HttpMethod.Post, "/platform/api/plans", admin, bad), HttpStatusCode.BadRequest));

        // PATCH: nama diganti, batas yang tidak dikirim menjadi tanpa batas.
        var patched = await SendAsync(HttpMethod.Patch, $"/platform/api/plans/{planId}", admin, new { name = $"{name}-2", maxProjects = 7 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var patchedBody = await BodyAsync(patched);
        Assert.Equal($"{name}-2", (string?)patchedBody["name"]);
        Assert.Equal(7, (int?)patchedBody["maxProjects"]);
        Assert.Null((int?)patchedBody["maxApiKeys"]);
        Assert.Null((long?)patchedBody["maxRequestsPerMonth"]);
        Assert.Null((long?)patchedBody["maxTokensPerMonth"]);

        // Plan yang dipakai tenant tidak bisa dihapus; plan bebas bisa.
        var slug = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/platform/api/tenants", admin,
            new { name = "T", slug, planId, ownerEmail = $"o-{slug}@example.test", ownerDisplayName = "Owner" })).StatusCode);
        Assert.Equal("plan_in_use", await CodeAsync(
            await SendAsync(HttpMethod.Delete, $"/platform/api/plans/{planId}", admin), HttpStatusCode.Conflict));

        var spare = await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/plans", admin, new { name = $"spare-{Guid.NewGuid():N}" }));
        var spareId = (long)spare["id"]!;
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/platform/api/plans/{spareId}", admin)).StatusCode);
        Assert.Equal("not_found", await CodeAsync(
            await SendAsync(HttpMethod.Delete, $"/platform/api/plans/{spareId}", admin), HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task Tenant_creation_is_transactional_and_owner_suspend_and_reset_follow()
    {
        var admin = await PlatformAdminAsync();
        var slug = Guid.NewGuid().ToString("N");
        var ownerEmail = $"owner-{slug}@example.test";

        var created = await SendAsync(HttpMethod.Post, "/platform/api/tenants", admin,
            new { name = $"Tenant {slug}", slug, ownerEmail, ownerDisplayName = "Owner One" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await BodyAsync(created);
        var tenant = body["tenant"]!;
        var tenantId = (Guid)tenant["id"]!;
        var inviteToken = (string)body["inviteToken"]!;
        Assert.Equal(("active", DbSeeder.DefaultPlanName), ((string?)tenant["status"], (string?)tenant["planName"]));

        // Detail, daftar, dan 404 untuk tenant asing.
        Assert.Equal($"Tenant {slug}", (string?)(await BodyAsync(await SendAsync(HttpMethod.Get, $"/platform/api/tenants/{tenantId}", admin)))["name"]);
        Assert.Contains((await BodyAsync(await SendAsync(HttpMethod.Get, "/platform/api/tenants", admin))).AsArray()!, t => (Guid?)t!["id"] == tenantId);
        Assert.Equal("not_found", await CodeAsync(
            await SendAsync(HttpMethod.Get, $"/platform/api/tenants/{Guid.NewGuid()}", admin), HttpStatusCode.NotFound));

        // Undangan owner dipakai sekali: setelah accept-invite, owner bisa login ke tenant itu.
        using (var accept = await Client.PostAsJsonAsync("/admin/api/auth/accept-invite", new { token = inviteToken, password = StrongPassword }))
            Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
        var me = await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/auth/me", await LoginAsync(ownerEmail)));
        Assert.Equal((tenantId, Roles.Owner, $"Tenant {slug}"), ((Guid?)me["tenantId"], (string?)me["role"], (string?)me["tenantName"]));

        // Slug ganda dan email owner yang sudah dipakai: seluruh transaksi dibatalkan, tanpa tenant yatim.
        Assert.Equal("already_exists", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/tenants", admin,
            new { name = "Other", slug, ownerEmail = $"x-{slug}@example.test", ownerDisplayName = "X" }), HttpStatusCode.Conflict));

        var orphanSlug = Guid.NewGuid().ToString("N");
        Assert.Equal("already_exists", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/tenants", admin,
            new { name = "Orphan", slug = orphanSlug, ownerEmail, ownerDisplayName = "Dup" }), HttpStatusCode.Conflict));
        await using (var ctx = Db.NewContext())
            Assert.False(await ctx.Tenants.AnyAsync(t => t.Slug == orphanSlug));

        // Owner kedua lewat invite-owner; email unik global tetap berlaku.
        var secondEmail = $"second-{slug}@example.test";
        var invited = await BodyAsync(await SendAsync(HttpMethod.Post, $"/platform/api/tenants/{tenantId}/invite-owner", admin,
            new { email = secondEmail, displayName = "Owner Two" }));
        var invitedUserId = (long)invited["user"]!["id"]!;
        Assert.Equal((secondEmail, Roles.Owner), ((string?)invited["user"]!["email"], (string?)invited["user"]!["role"]));
        Assert.False(string.IsNullOrEmpty((string?)invited["inviteToken"]));
        Assert.Equal("already_exists", await CodeAsync(await SendAsync(HttpMethod.Post, $"/platform/api/tenants/{tenantId}/invite-owner", admin,
            new { email = secondEmail, displayName = "Again" }), HttpStatusCode.Conflict));

        // Reset password hanya untuk anggota tenant itu: user tenant lain dan platform admin = 404.
        var other = await NewUserAsync(Roles.Owner);
        long adminUserId;
        await using (var ctx = Db.NewContext())
            adminUserId = await ctx.Users.Where(u => u.Role == Roles.PlatformAdmin).Select(u => u.Id).FirstAsync();
        Assert.Equal("not_found", await CodeAsync(
            await SendAsync(HttpMethod.Post, $"/platform/api/tenants/{tenantId}/reset-password/{other.UserId}", admin), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await CodeAsync(
            await SendAsync(HttpMethod.Post, $"/platform/api/tenants/{tenantId}/reset-password/{adminUserId}", admin), HttpStatusCode.NotFound));

        var resetBody = await BodyAsync(await SendAsync(HttpMethod.Post, $"/platform/api/tenants/{tenantId}/reset-password/{invitedUserId}", admin));
        var resetToken = (string?)resetBody["token"]!;
        Assert.False(string.IsNullOrEmpty(resetToken));
        using (var redeem = await Client.PostAsJsonAsync("/admin/api/auth/reset-password", new { token = resetToken, password = "another-strong-pass-9" }))
            Assert.Equal(HttpStatusCode.NoContent, redeem.StatusCode);
        await LoginAsync(secondEmail, "another-strong-pass-9");

        // Suspend: data plane mati, mutasi tenant ditolak, baca tetap boleh; lalu diaktifkan lagi.
        var s = await SetupAsync();
        var scenarioTenant = await TenantPublicIdAsync(s.TenantId);
        var (_, _, scenarioOwnerEmail) = await NewUserAsync(Roles.Owner, s.TenantId);
        var scenarioToken = await LoginAsync(scenarioOwnerEmail);

        var suspended = await BodyAsync(await SendAsync(HttpMethod.Patch, $"/platform/api/tenants/{scenarioTenant}", admin, new { status = TenantStatuses.Suspended }));
        Assert.Equal("suspended", (string?)suspended["status"]);
        using (var chat = await PostChatAsync(s.ApiKey, Chat()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, chat.StatusCode);
            Assert.Equal("tenant_suspended", ErrorCode(await BodyAsync(chat)));
        }
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/providers", scenarioToken)).StatusCode);
        Assert.Equal("tenant_suspended", await CodeAsync(await SendAsync(HttpMethod.Post, "/admin/api/providers", scenarioToken,
            new { templateCode = DbSeeder.OpenCodeTemplateCode, name = "blocked", apiKey = "sk-blocked-key-1234" }), HttpStatusCode.Forbidden));

        // Status/plan tidak valid ditolak; rename + reaktivasi jalan.
        Assert.Equal("invalid_status", await CodeAsync(
            await SendAsync(HttpMethod.Patch, $"/platform/api/tenants/{scenarioTenant}", admin, new { status = "paused" }), HttpStatusCode.BadRequest));
        Assert.Equal("not_found", await CodeAsync(
            await SendAsync(HttpMethod.Patch, $"/platform/api/tenants/{scenarioTenant}", admin, new { planId = 999_999 }), HttpStatusCode.NotFound));

        var renamed = await BodyAsync(await SendAsync(HttpMethod.Patch, $"/platform/api/tenants/{scenarioTenant}", admin,
            new { name = "Renamed", status = TenantStatuses.Active }));
        Assert.Equal(("Renamed", "active"), ((string?)renamed["name"], (string?)renamed["status"]));
        using (var chat = await PostChatAsync(s.ApiKey, Chat())) Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
    }

    [Fact]
    public async Task Manual_catalog_rows_validate_prices_tiers_and_are_exposed_to_tenant_readers()
    {
        var admin = await PlatformAdminAsync();
        var slug = Guid.NewGuid().ToString("N");
        var code = $"tpl-{slug[..8]}";

        var template = await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/templates", admin, new
        {
            code, name = "Vendor", type = "openai", defaultBaseUrl = "https://vendor.test/v1/",
            authHeader = "Authorization", authPrefix = "Bearer ", enabled = true,
        }));
        var templateId = (long)template["id"]!;
        Assert.Equal((code, "openai", true), ((string?)template["code"], (string?)template["type"], (bool?)template["enabled"]));

        Assert.Equal("invalid_type", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/templates", admin,
            new { code = code + "-b", name = "X", type = "anthropic", defaultBaseUrl = "https://vendor.test/v1/" }), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_base_url", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/templates", admin,
            new { code = code + "-c", name = "X", defaultBaseUrl = "ftp://vendor.test/v1/" }), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_sync_kind", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/templates", admin,
            new { code = code + "-d", name = "X", defaultBaseUrl = "https://vendor.test/v1/", syncKind = "other" }), HttpStatusCode.BadRequest));
        Assert.Equal("already_exists", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/templates", admin,
            new { code, name = "Dup", defaultBaseUrl = "https://vendor.test/v1/" }), HttpStatusCode.Conflict));

        // PUT menimpa seluruh kolom; id di badan diabaikan.
        var updated = await BodyAsync(await SendAsync(HttpMethod.Put, $"/platform/api/templates/{templateId}", admin,
            new { id = 999_999, code = code + "-2", name = "Vendor 2", defaultBaseUrl = "https://vendor.test/v2/", enabled = false }));
        Assert.Equal((templateId, code + "-2", "Vendor 2", false),
            ((long?)updated["id"], (string?)updated["code"], (string?)updated["name"], (bool?)updated["enabled"]));
        code += "-2";

        // Baris katalog manual lengkap dengan tier; source dari klien diabaikan dan selalu manual.
        var upstream = $"model-{slug[..8]}";
        Dictionary<string, object?> Row(string upstreamModel, string displayName, decimal outputPrice, string source = "discovered") => new()
        {
            ["templateCode"] = code,
            ["upstreamModel"] = upstreamModel,
            ["displayName"] = displayName,
            ["apiFamily"] = ApiFamilies.OpenAiChat,
            ["contextWindow"] = 200_000,
            ["maxInputTokens"] = 180_000,
            ["maxOutputTokens"] = 8_000,
            ["inputPricePer1M"] = 0.3m,
            ["outputPricePer1M"] = outputPrice,
            ["cacheReadPricePer1M"] = 0.06m,
            ["cacheWritePricePer1M"] = 0.07m,
            ["tiers"] = new Dictionary<string, object?>[]
            {
                new() { ["minInputTokens"] = 128_000, ["input"] = 0.6m, ["output"] = 2.4m, ["cacheRead"] = null, ["cacheWrite"] = null },
            },
            ["currency"] = "USD",
            ["supportsTools"] = true,
            ["supportsVision"] = true,
            ["supportsReasoning"] = true,
            ["source"] = source,
            ["enabled"] = true,
        };
        Dictionary<string, object?> Bad(Action<Dictionary<string, object?>> mutate)
        {
            var row = Row($"bad-{Guid.NewGuid():N}"[..12], "Bad", 1m);
            mutate(row);
            return row;
        }

        var created = await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin, Row(upstream, "Model X", 1.2m)));
        var rowId = (long)created["id"]!;
        Assert.Equal(("manual", 0.3m, 0.06m), ((string?)created["source"], (decimal?)created["inputPricePer1M"], (decimal?)created["cacheReadPricePer1M"]));
        Assert.Equal(128_000, (int?)created["tiers"]!.AsArray()[0]!["minInputTokens"]);

        // Upsert pada pasangan (template, model) yang sama memperbarui baris, bukan menambah baris baru.
        var upserted = await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin, Row(upstream, "Model X2", 1.5m, "seed")));
        Assert.Equal((rowId, "Model X2", "manual", 1.5m),
            ((long?)upserted["id"], (string?)upserted["displayName"], (string?)upserted["source"], (decimal?)upserted["outputPricePer1M"]));
        await using (var ctx = Db.NewContext())
            Assert.Equal(1, await ctx.CatalogModels.CountAsync(m => m.UpstreamModel == upstream));

        // PUT: pasangan unik dijaga (409), pemindahan template ikut divalidasi.
        var secondUpstream = upstream + "-2";
        var secondId = (long)(await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin, Row(secondUpstream, "Model Y", 2m))))["id"]!;
        Assert.Equal("already_exists", await CodeAsync(await SendAsync(HttpMethod.Put, $"/platform/api/catalog/{secondId}", admin,
            Row(upstream, "Clash", 2m)), HttpStatusCode.Conflict));
        var moved = await BodyAsync(await SendAsync(HttpMethod.Put, $"/platform/api/catalog/{secondId}", admin, Row(secondUpstream, "Model Y2", 2m)));
        Assert.Equal((secondId, "Model Y2"), ((long?)moved["id"], (string?)moved["displayName"]));

        // Validasi harga/tier/keluarga API/currency/upstream/template/displayName.
        Assert.Equal("invalid_price", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["inputPricePer1M"] = -1m)), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_price", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["tiers"] = new Dictionary<string, object?>[] { new() { ["minInputTokens"] = 0, ["input"] = 0m, ["output"] = 0m } })), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_price", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["tiers"] = new Dictionary<string, object?>[]
            {
                new() { ["minInputTokens"] = 1000, ["input"] = 1m, ["output"] = 1m },
                new() { ["minInputTokens"] = 1000, ["input"] = 2m, ["output"] = 2m },
            })), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_api_family", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["apiFamily"] = "made_up")), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_currency", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["currency"] = "usd")), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_limit", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["maxOutputTokens"] = 0)), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_upstream_model", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["upstreamModel"] = "has space")), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_display_name", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["displayName"] = " ")), HttpStatusCode.BadRequest));
        Assert.Equal("not_found", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin,
            Bad(b => b["templateCode"] = "nope-nope")), HttpStatusCode.NotFound));

        // Baris manual ini terlihat oleh tenant lewat rute katalog biasa.
        var (_, _, ownerEmail) = await NewUserAsync(Roles.Owner);
        var tenantToken = await LoginAsync(ownerEmail);
        var models = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/catalog/models?templateCode={code}", tenantToken));
        var visible = Assert.Single(models.AsArray()!, m => (string?)m!["upstreamModel"] == upstream);
        Assert.Equal(("manual", 1.5m, 0.3m), ((string?)visible!["source"], (decimal?)visible["outputPricePer1M"], (decimal?)visible["inputPricePer1M"]));
        Assert.Equal(128_000, (int?)visible["tiers"]!.AsArray()[0]!["minInputTokens"]);

        // Tanpa filter templateCode platform admin melihat semua baris.
        var all = await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/catalog/models", admin));
        Assert.Contains(all.AsArray()!, m => (long?)m!["id"] == rowId);
    }

    [Fact]
    public async Task Catalog_sync_uses_the_default_url_and_never_stores_or_audits_the_service_key()
    {
        var admin = await PlatformAdminAsync();
        const string serviceKey = "sk-secret-service-key";
        Upstream.Respond = _ => FakeUpstream.Json(200, SyncConfig);

        var synced = await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog/sync", admin,
            new { serviceKey, url = "https://catalog.test/c" }));
        Assert.Equal((1, 1, 0, 0, 0), ((int?)synced["templatesAdded"], (int?)synced["modelsAdded"], (int?)synced["modelsUpdated"],
            (int?)synced["modelsSkippedManual"], (int?)synced["modelsUnsupported"]));
        Assert.Equal($"Bearer {serviceKey}", Upstream.Requests.Last().Headers["authorization"]);

        // Tanpa url: sync_url template bawaan yang dipakai.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/platform/api/catalog/sync", admin, new { serviceKey })).StatusCode);
        Assert.Equal(DbSeeder.OpenCodeConfigUrl, Upstream.Requests.Last().Uri.ToString());

        // Baris manual tidak ditimpa sinkronisasi, dan tidak ada service key yang tersimpan/teraudit.
        var manual = await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog", admin, new
        {
            templateCode = "opencode-go", upstreamModel = "glm-5.3-flash", displayName = "Manual Override",
            inputPricePer1M = 9m, outputPricePer1M = 9m, enabled = true,
        }));
        var manualId = (long)manual["id"]!;
        var second = await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog/sync", admin, new { serviceKey, url = "https://catalog.test/c" }));
        Assert.Equal(1, (int?)second["modelsSkippedManual"]);

        await using (var ctx = Db.NewContext())
        {
            var stored = await ctx.CatalogModels.SingleAsync(m => m.UpstreamModel == "glm-5.3-flash");
            Assert.Equal((manualId, "manual", 9m), (stored.Id, stored.Source, stored.InputPricePer1M));
            var details = string.Join("\n", await ctx.AuditLogs.AsNoTracking().Select(a => a.DetailJson ?? "").ToListAsync());
            Assert.DoesNotContain(serviceKey, details);
        }

        // Key kosong dan URL yang tidak aman ditolak tanpa menyentuh upstream.
        var before = Upstream.Requests.Count;
        Assert.Equal("missing_key", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog/sync", admin,
            new { serviceKey = "", url = "https://catalog.test/c" }), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_catalog_url", await CodeAsync(await SendAsync(HttpMethod.Post, "/platform/api/catalog/sync", admin,
            new { serviceKey, url = "http://catalog.test/c" }), HttpStatusCode.BadRequest));
        Assert.Equal(before, Upstream.Requests.Count);
    }

    [Fact]
    public async Task Usage_audit_and_one_time_secrets_stay_scoped_and_leak_nothing()
    {
        var admin = await PlatformAdminAsync();
        var s = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var ctx = Db.NewContext(s.TenantId))
        {
            ctx.UsageDailies.Add(new UsageDaily
            {
                TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = s.ModelId, Day = today,
                Requests = 4, InputTokens = 40, OutputTokens = 8, Cost = 1.25m,
            });
            await ctx.SaveChangesAsync();
        }

        // Overview usage: per tenant, memakai public_id (bukan id internal), label nama tenant.
        var usage = await BodyAsync(await SendAsync(HttpMethod.Get, $"/platform/api/usage?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}", admin));
        var scenarioPublicId = await TenantPublicIdAsync(s.TenantId);
        var row = Assert.Single(usage.AsArray()!, r => (Guid?)r!["key"] == scenarioPublicId);
        Assert.Equal((4L, 40L, 8L, 1.25m, "T"),
            ((long?)row!["requests"], (long?)row["inputTokens"], (long?)row["outputTokens"], (decimal?)row["cost"], (string?)row["label"]));
        Assert.All(usage.AsArray()!, r => Assert.True(Guid.TryParse((string?)r!["key"], out _))); // key = public_id, bukan id internal

        // Aksi platform (tenant_id NULL) dan satu aksi tenant sebagai pembanding.
        var slug = Guid.NewGuid().ToString("N");
        var ownerEmail = $"audit-{slug}@example.test";
        var inviteToken = (string)(await BodyAsync(await SendAsync(HttpMethod.Post, "/platform/api/tenants", admin,
            new { name = "Audit", slug, ownerEmail, ownerDisplayName = "Audit Owner" })))["inviteToken"]!;
        var (_, _, scenarioOwnerEmail) = await NewUserAsync(Roles.Owner, s.TenantId);
        var scenarioToken = await LoginAsync(scenarioOwnerEmail);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/api/providers", scenarioToken,
            new { templateCode = DbSeeder.OpenCodeTemplateCode, name = "audited", apiKey = "sk-audited-key-1234" })).StatusCode);

        var audit = await BodyAsync(await SendAsync(HttpMethod.Get, "/platform/api/audit?pageSize=200", admin));
        var items = audit["items"]!.AsArray();
        Assert.Equal(1, (int?)audit["pageNumber"]);
        Assert.Equal(200, (int?)audit["pageSize"]);
        Assert.True((long?)audit["total"] >= items.Count);
        Assert.Contains(items, i => (string?)i!["action"] == "tenant.create" && (string?)i!["entity"] == "tenant");
        Assert.Contains(items, i => (string?)i!["action"] == "provider.create"); // aksi tenant ikut terlihat, detailnya tidak
        Assert.All(items, i => Assert.Null(i!["detailJson"])); // metadata saja, tanpa detail milik tenant

        // Token undangan dan token reset tidak pernah masuk ke baris audit.
        long auditOwnerUserId;
        Guid auditTenantId;
        await using (var ctx = Db.NewContext())
        {
            auditOwnerUserId = await ctx.Users.Where(u => u.Email == ownerEmail).Select(u => u.Id).SingleAsync();
            auditTenantId = await ctx.Tenants.Where(t => t.Slug == slug).Select(t => t.PublicId).SingleAsync();
        }
        var resetToken = (string)(await BodyAsync(await SendAsync(HttpMethod.Post,
            $"/platform/api/tenants/{auditTenantId}/reset-password/{auditOwnerUserId}", admin)))["token"]!;
        Assert.False(string.IsNullOrEmpty(resetToken));

        await using (var ctx = Db.NewContext())
        {
            var details = string.Join("\n", await ctx.AuditLogs.AsNoTracking().Select(a => a.DetailJson ?? "").ToListAsync());
            Assert.DoesNotContain(inviteToken, details);
            Assert.DoesNotContain(resetToken, details);
            var actions = await ctx.AuditLogs.AsNoTracking().Select(a => a.Action).ToListAsync();
            Assert.Contains("tenant.create", actions);
            Assert.Contains("tenant.reset_password", actions);
        }
    }
}
