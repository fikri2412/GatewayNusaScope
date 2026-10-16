using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.WebSearch;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

/// <summary>
/// Endpoint admin /admin/providers (+ alias /admin/api/search-providers) dan data plane /v1/search-/v1/extract.
/// Upstream HTTP pencarian diarahkan ke <see cref="FakeUpstream"/> oleh <see cref="GatewayFactory"/>.
/// </summary>
public class WebSearchEndpointTests(TestDb db) : GatewayTestBase(db)
{
    private const string TavilyOk =
        """{"results":[{"title":"T","url":"https://example.com","content":"snippet","score":0.5}]}""";

    [Fact]
    public async Task Status_and_manual_reset_match_budget_rollover_and_probe_rules()
    {
        var (scenario, token) = await OwnerTenantAsync();
        Factory.Services.GetRequiredService<IOptions<WebSearchOptions>>().Value.BudgetThreshold = 0.8m;
        var now = DateTime.UtcNow;
        var ids = new Dictionary<string, Guid>();
        foreach (var name in new[] { "Threshold", "Broken", "Rollover", "Manual", "Probe" })
        {
            var created = await SendAsync(HttpMethod.Post, "/admin/providers", token, new
            {
                type = "tavily", name, apiKey = "audit-test-key-only-123456",
                monthlyBudget = 10, resetPolicy = name == "Manual" ? "manual" : "first_of_month_utc",
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            ids[name] = (Guid)(await BodyAsync(created))["id"]!;
        }
        await WithServicesAsync(scenario.TenantId, async (_, db) =>
        {
            foreach (var config in await db.SearchProviderConfigs.ToListAsync())
            {
                var state = await db.SearchProviderStates.SingleAsync(s => s.ProviderId == config.Id);
                state.PeriodStart = now;
                state.SuccessCount = 9;
                state.UsedThisPeriod = config.Name == "Threshold" ? 8m : config.Name == "Probe" ? 0m : 10m;
                if (config.Name == "Broken") state.Status = SearchProviderStatuses.Broken;
                if (config.Name is "Rollover" or "Manual") state.Status = SearchProviderStatuses.Exhausted;
                if (config.Name == "Rollover") state.PeriodStart = now.AddMonths(-1);
                if (config.Name == "Probe")
                {
                    state.Status = SearchProviderStatuses.RateLimited;
                    state.UnavailableUntil = now.AddMinutes(-1);
                    state.ProbeLeaseId = Guid.NewGuid();
                    state.ProbeLeaseUntil = now.AddMinutes(5);
                }
            }
            await db.SaveChangesAsync();
            return true;
        });
        var response = await SendAsync(HttpMethod.Get, "/admin/providers/status", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (JsonArray)await BodyAsync(response);
        JsonNode Row(string name) => rows.Single(r => (string?)r?["name"] == name)!;
        Assert.Equal("exhausted", (string?)Row("Threshold")["status"]);
        Assert.Equal("broken", (string?)Row("Broken")["status"]);
        Assert.Null(Row("Broken")["nextRetryTime"]);
        Assert.Equal("available", (string?)Row("Rollover")["status"]);
        Assert.Equal(0m, (decimal)Row("Rollover")["usedThisPeriod"]!);
        Assert.Equal(10m, (decimal)Row("Rollover")["estimatedRemainingCredits"]!);
        Assert.Equal("rate_limited", (string?)Row("Probe")["status"]);
        Assert.Equal(now.AddMinutes(5), (DateTime)Row("Probe")["nextRetryTime"]!);
        Assert.EndsWith("Z", (string)Row("Probe")["nextRetryTime"]!);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"/admin/providers/{ids["Manual"]}/reset", token)).StatusCode);
        await WithServicesAsync(scenario.TenantId, async (_, db) =>
        {
            var config = await db.SearchProviderConfigs.SingleAsync(c => c.PublicId == ids["Manual"]);
            var state = await db.SearchProviderStates.SingleAsync(s => s.ProviderId == config.Id);
            Assert.Equal(0m, state.UsedThisPeriod);
            Assert.True(state.PeriodStart >= now);
            Assert.Equal(9, state.SuccessCount);
            Assert.Equal(SearchProviderStatuses.Available, state.Status);
            return true;
        });
    }

    private async Task<string> LoginAsync(string email)
    {
        var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["accessToken"]!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    private async Task<(Scenario S, string Token)> OwnerTenantAsync()
    {
        var s = await SetupAsync();
        var (_, _, email) = await NewUserAsync(Roles.Owner, s.TenantId);
        return (s, await LoginAsync(email));
    }

    private async Task<Guid> CreateAsync(string token, object payload)
    {
        var response = await SendAsync(HttpMethod.Post, "/admin/providers", token, payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((string)(await BodyAsync(response))["id"]!);
    }

    [Fact]
    public async Task Stale_admin_update_gets_409_instead_of_overwriting_a_newer_key()
    {
        var (scenario, token) = await OwnerTenantAsync();
        var id = await CreateAsync(token, new { type = SearchProviderTypes.Tavily, apiKey = "tvly-first-key-111111" });

        // Dua pembaca memuat config yang sama; penulis pertama menang, yang kedua memegang token basi.
        await using var stale = Factory.Services.CreateAsyncScope();
        var staleDb = stale.ServiceProvider.GetRequiredService<GatewayDbContext>();
        staleDb.CurrentTenantId = scenario.TenantId;
        var staleConfig = await staleDb.SearchProviderConfigs.SingleAsync(c => c.PublicId == id);

        var rotated = await SendAsync(HttpMethod.Put, $"/admin/providers/{id}", token,
            new { apiKey = "tvly-second-key-222222" });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        staleConfig.KeyHint = "oldx";
        staleConfig.UpdatedAt = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());

        var read = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/providers/{id}", token));
        Assert.Equal("2222", (string)read["keyHint"]!); // kunci baru tidak tertimpa
    }

    [Fact]
    public async Task Moving_a_search_provider_to_another_host_requires_a_new_key()
    {
        var (_, token) = await OwnerTenantAsync();
        var id = await CreateAsync(token, new { type = SearchProviderTypes.Tavily, apiKey = "tvly-first-key-111111" });

        var rejected = await SendAsync(HttpMethod.Put, $"/admin/providers/{id}", token, new { baseUri = "https://proxy.example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("credential_required_on_base_url_change", ErrorCode(await BodyAsync(rejected)));
        var unchanged = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/providers/{id}", token));
        Assert.Equal(("https://api.tavily.com", "1111"), ((string)unchanged["baseUri"]!, (string)unchanged["keyHint"]!)); // tidak ada yang berubah

        var accepted = await SendAsync(HttpMethod.Put, $"/admin/providers/{id}", token,
            new { baseUri = "https://proxy.example.com", apiKey = "tvly-second-key-222222" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var moved = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/providers/{id}", token));
        Assert.Equal(("https://proxy.example.com", "2222"), ((string)moved["baseUri"]!, (string)moved["keyHint"]!));
    }

    [Fact]
    public async Task Admin_can_crud_search_providers_with_defaults_and_masked_key()
    {
        var (_, token) = await OwnerTenantAsync();

        // Registrasi hanya butuh type + key: nama default = tipe, BaseUri default dari metadata adapter.
        var response = await SendAsync(HttpMethod.Post, "/admin/providers", token,
            new { type = SearchProviderTypes.Tavily, apiKey = "tvly-secret-12345678" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await BodyAsync(response);
        var raw = await response.Content.ReadAsStringAsync();

        var id = Guid.Parse((string)created["id"]!);
        Assert.Equal("tavily", (string)created["name"]!);
        Assert.Equal("https://api.tavily.com", (string)created["baseUri"]!);
        Assert.True((bool)created["isEnabled"]!);
        Assert.Equal(1, (int)created["weight"]!);
        Assert.Null(created["apiKey"]);
        Assert.Equal("5678", (string)created["keyHint"]!);
        Assert.DoesNotContain("tvly-secret-12345678", raw);
        Assert.DoesNotContain(Factory.Logs.Messages, m => m.Contains("tvly-secret-12345678"));

        // List (route baru + alias lama) tidak pernah memuat kunci mentah.
        var list = (JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers", token)));
        Assert.Contains(list, item => (string)item!["id"]! == id.ToString());
        var alias = await SendAsync(HttpMethod.Get, "/admin/api/search-providers", token);
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
        Assert.DoesNotContain("tvly-secret-12345678", await alias.Content.ReadAsStringAsync());

        // Alias lama juga menerima penulisan.
        var aliasCreate = await SendAsync(HttpMethod.Post, "/admin/api/search-providers", token,
            new { type = SearchProviderTypes.Exa, name = "Lewat Alias", apiKey = "exa-secret-12345678" });
        Assert.Equal(HttpStatusCode.Created, aliasCreate.StatusCode);

        var single = await SendAsync(HttpMethod.Get, $"/admin/providers/{id}", token);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Equal("5678", (string)(await BodyAsync(single))["keyHint"]!);

        // Status tersedia, tanpa anggaran -> sisa kredit tidak diketahui.
        var status = (JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers/status", token)));
        var statusItem = status.First(item => (string)item!["id"]! == id.ToString());
        Assert.Equal(SearchProviderStatuses.Available, (string)statusItem!["status"]!);
        Assert.Null(statusItem["estimatedRemainingCredits"]);
        Assert.Equal(0m, (decimal)statusItem["usedThisPeriod"]!);

        // Disable -> status disabled; enable -> available; reset -> sukses; delete -> hilang dari daftar.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/disable", token)).StatusCode);
        status = (JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers/status", token)));
        Assert.Equal(SearchProviderStatuses.Disabled,
            (string)status.First(item => (string)item!["id"]! == id.ToString())!["status"]!);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/enable", token)).StatusCode);
        var reset = await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/reset", token);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal("reset_successful", (string)(await BodyAsync(reset))["status"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/providers/{id}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/providers/{id}", token)).StatusCode);
        Assert.DoesNotContain((JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers", token))),
            item => (string)item!["id"]! == id.ToString());
    }

    [Fact]
    public async Task Create_validates_type_length_numeric_capability_and_json_rules()
    {
        var (s, token) = await OwnerTenantAsync();

        async Task Bad(object payload, string code)
        {
            var response = await SendAsync(HttpMethod.Post, "/admin/providers", token, payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, ErrorCode(await BodyAsync(response)));
        }

        await Bad(new { type = "brave", apiKey = "k-123456789012" }, "invalid_type");
        await Bad(new { type = SearchProviderTypes.Tavily }, "invalid_api_key");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", name = new string('n', 101) }, "invalid_name");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", weight = 0 }, "invalid_weight");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", weight = 101 }, "invalid_weight");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", capabilities = "browse" }, "invalid_capabilities");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", monthlyBudget = -1m }, "invalid_budget");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", costPerSearch = -1m }, "invalid_cost");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", baseUri = "http://api.tavily.com" }, "invalid_base_uri");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", baseUri = "https://127.0.0.1:8080" }, "invalid_base_uri");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", errorMappingOverride = "not json" }, "invalid_error_mapping");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", errorMappingOverride = "{}" }, "invalid_error_mapping");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", errorMappingOverride = """{"unknown":[1]}""" }, "invalid_error_mapping");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", errorMappingOverride = """{"brokenStatusCodes":[42]}""" }, "invalid_error_mapping");
        await Bad(new { type = SearchProviderTypes.Tavily, apiKey = "k", errorMappingOverride = """{"brokenStatusCodes":"401"}""" }, "invalid_error_mapping");

        // Tidak ada baris yang tercipta dari percobaan gagal (validasi sebelum insert).
        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            Assert.Equal(0, await ctx.SearchProviderConfigs.CountAsync());
            return 0;
        });

        var ok = await SendAsync(HttpMethod.Post, "/admin/providers", token, new
        {
            type = SearchProviderTypes.Tavily, apiKey = "k-123456789012",
            capabilities = " extract , search ", errorMappingOverride = """{"brokenStatusCodes":[401,403]}""",
        });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var body = await BodyAsync(ok);
        Assert.Equal("search,extract", (string)body["capabilities"]!);
    }

    [Fact]
    public async Task Duplicate_name_is_409_and_create_is_atomic()
    {
        var (s, token) = await OwnerTenantAsync();
        var id = await CreateAsync(token, new
        {
            type = SearchProviderTypes.Exa, name = "Duplikat", apiKey = "exa-secret-12345678",
        });

        var duplicate = await SendAsync(HttpMethod.Post, "/admin/providers", token, new
        {
            type = SearchProviderTypes.Exa, name = "Duplikat", apiKey = "exa-secret-12345678",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("provider_exists", ErrorCode(await BodyAsync(duplicate)));

        // Setiap config selalu lahir dengan state-nya.
        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            var config = await ctx.SearchProviderConfigs.SingleAsync(c => c.PublicId == id);
            var state = await ctx.SearchProviderStates.SingleAsync(st => st.ProviderId == config.Id);
            Assert.Equal(SearchProviderStatuses.Available, state.Status);
            return 0;
        });
    }

    [Fact]
    public async Task Short_keys_are_never_disclosed()
    {
        var (_, token) = await OwnerTenantAsync();
        var response = await SendAsync(HttpMethod.Post, "/admin/providers", token, new
        {
            type = SearchProviderTypes.Firecrawl, name = "Pendek", apiKey = "short-key",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"keyHint\":\"****\"", text);
        Assert.DoesNotContain("short-key", text);

        var listText = await (await SendAsync(HttpMethod.Get, "/admin/providers", token)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("short-key", listText);
    }

    [Fact]
    public async Task Viewer_and_anonymous_cannot_access_admin_search_providers()
    {
        var s = await SetupAsync();
        var (_, _, viewerEmail) = await NewUserAsync(Roles.Viewer, s.TenantId);
        var viewer = await LoginAsync(viewerEmail);
        var (_, _, adminEmail) = await NewUserAsync(Roles.Admin, s.TenantId);
        var admin = await LoginAsync(adminEmail);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/admin/providers", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/providers", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/providers/status", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, "/admin/providers", viewer, new { type = SearchProviderTypes.Tavily, apiKey = "k-123456789012" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, $"/admin/providers/{Guid.NewGuid()}/reset", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Get, "/admin/api/search-providers", viewer)).StatusCode);

        // admin (bukan hanya owner) boleh mengelola.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/providers", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Get, "/admin/api/search-providers", admin)).StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_provider_is_404_for_every_action()
    {
        var (_, tokenA) = await OwnerTenantAsync();
        var id = await CreateAsync(tokenA, new
        {
            type = SearchProviderTypes.Tavily, name = "Milik A", apiKey = "tvly-secret-12345678",
        });

        var b = await SetupAsync();
        var (_, _, emailB) = await NewUserAsync(Roles.Owner, b.TenantId);
        var tokenB = await LoginAsync(emailB);

        Assert.DoesNotContain((JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers", tokenB))),
            item => (string)item!["id"]! == id.ToString());

        var actions = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/admin/providers/{id}", null),
            (HttpMethod.Put, $"/admin/providers/{id}", new { name = "curi" }),
            (HttpMethod.Delete, $"/admin/providers/{id}", null),
            (HttpMethod.Post, $"/admin/providers/{id}/enable", null),
            (HttpMethod.Post, $"/admin/providers/{id}/disable", null),
            (HttpMethod.Post, $"/admin/providers/{id}/reset", null),
            (HttpMethod.Post, $"/admin/providers/{id}/test", null),
        };
        foreach (var (method, path, body) in actions)
        {
            var response = await SendAsync(method, path, tokenB, body);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Suspended_tenant_cannot_mutate_but_reads_are_allowed_with_no_store()
    {
        var (s, token) = await OwnerTenantAsync();
        var id = await CreateAsync(token, new
        {
            type = SearchProviderTypes.Tavily, name = "Suspend", apiKey = "tvly-secret-12345678",
        });

        await WithServicesAsync(s.TenantId, (_, ctx) =>
            ctx.Tenants.Where(t => t.Id == s.TenantId)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, TenantStatuses.Suspended)));

        var denied = await SendAsync(HttpMethod.Post, "/admin/providers", token, new
        {
            type = SearchProviderTypes.Exa, apiKey = "exa-secret-12345678",
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("tenant_suspended", ErrorCode(await BodyAsync(denied)));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/admin/providers/{id}", token)).StatusCode);

        var list = await SendAsync(HttpMethod.Get, "/admin/providers", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("no-store", list.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Admin_providers_body_over_the_limit_is_rejected_with_413()
    {
        var filler = new string('x', (1024 * 1024) + 16);
        using var content = new StringContent($$"""{"type":"tavily","apiKey":"{{filler}}"}""", Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/admin/providers", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request_too_large", ErrorCode(await BodyAsync(response)));
    }

    [Fact]
    public async Task Test_endpoint_uses_hub_counts_one_call_and_sanitizes_errors()
    {
        var (s, token) = await OwnerTenantAsync();
        var id = await CreateAsync(token, new
        {
            type = SearchProviderTypes.Tavily, name = "Uji", apiKey = "tvly-secret-12345678", costPerSearch = 3m,
        });

        // 1. Sukses: satu panggilan murah tercatat sebagai pemakaian + biaya.
        Upstream.Respond = _ => FakeUpstream.Json(200, TavilyOk);
        var ok = await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/test", token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okText = await ok.Content.ReadAsStringAsync();
        Assert.True((bool)(await BodyAsync(ok))["success"]!);
        Assert.DoesNotContain("tvly-secret-12345678", okText);

        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            var config = await ctx.SearchProviderConfigs.SingleAsync(c => c.PublicId == id);
            var state = await ctx.SearchProviderStates.SingleAsync(st => st.ProviderId == config.Id);
            Assert.Equal(1, state.SuccessCount);
            Assert.Equal(3m, state.UsedThisPeriod);
            return 0;
        });

        // 2. Kunci ditolak: pesan tetap fixed/sanitized, bukan body penyedia; status jadi broken.
        Upstream.Respond = _ => FakeUpstream.Json(401, """{"detail":"Invalid API key tvly-secret-12345678"}""");
        var failed = await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/test", token);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var failedBody = await BodyAsync(failed);
        Assert.False((bool)failedBody["success"]!);
        var message = (string)failedBody["message"]!;
        Assert.DoesNotContain("Invalid API key", message);
        Assert.DoesNotContain("tvly-secret-12345678", message);

        var status = (JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers/status", token)));
        var item = status.First(i => (string)i!["id"]! == id.ToString())!;
        Assert.Equal(SearchProviderStatuses.Broken, (string)item["status"]!);
        Assert.Null(item["nextRetryTime"]);

        // 3. Kunci baru memulihkan provider broken.
        var updated = await SendAsync(HttpMethod.Put, $"/admin/providers/{id}", token,
            new { apiKey = "tvly-rotated-87654321" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("4321", (string)(await BodyAsync(updated))["keyHint"]!);
        status = (JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers/status", token)));
        Assert.Equal(SearchProviderStatuses.Available,
            (string)status.First(i => (string)i!["id"]! == id.ToString())!["status"]!);

        // Kunci dan body penyedia tidak pernah masuk log host.
        Assert.DoesNotContain(Factory.Logs.Messages, m => m.Contains("tvly-secret-12345678"));
        Assert.DoesNotContain(Factory.Logs.Messages, m => m.Contains("Invalid API key"));
    }

    [Fact]
    public async Task Reset_restores_broken_but_disabled_stays_disabled()
    {
        var (s, token) = await OwnerTenantAsync();
        var id = await CreateAsync(token, new
        {
            type = SearchProviderTypes.Exa, name = "Reset", apiKey = "exa-secret-12345678",
        });

        async Task BreakItAsync()
        {
            await WithServicesAsync(s.TenantId, async (_, ctx) =>
            {
                var config = await ctx.SearchProviderConfigs.SingleAsync(c => c.PublicId == id);
                var state = await ctx.SearchProviderStates.SingleAsync(st => st.ProviderId == config.Id);
                state.Status = SearchProviderStatuses.Broken;
                state.UnavailableUntil = DateTime.MaxValue;
                state.ConsecutiveFailures = 3;
                state.LastError = "rusak";
                state.Generation++;
                await ctx.SaveChangesAsync();
                return 0;
            });
        }

        async Task<string> StatusAsync() =>
            (string)((JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers/status", token))))
                .First(i => (string)i!["id"]! == id.ToString())!["status"]!;

        await BreakItAsync();
        Assert.Equal(SearchProviderStatuses.Broken, await StatusAsync());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/reset", token)).StatusCode);
        Assert.Equal(SearchProviderStatuses.Available, await StatusAsync());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/disable", token)).StatusCode);
        await BreakItAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/reset", token)).StatusCode);
        Assert.Equal(SearchProviderStatuses.Disabled, await StatusAsync());

        // Rotasi kunci saat disabled juga tetap disabled.
        var rotate = await SendAsync(HttpMethod.Put, $"/admin/providers/{id}", token, new { apiKey = "exa-rotated-87654321" });
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        Assert.Equal(SearchProviderStatuses.Disabled, await StatusAsync());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/enable", token)).StatusCode);
        Assert.Equal(SearchProviderStatuses.Available, await StatusAsync());
    }

    [Fact]
    public async Task Admin_ops_bump_generation_and_audit_never_records_keys()
    {
        var (s, token) = await OwnerTenantAsync();
        var id = await CreateAsync(token, new
        {
            type = SearchProviderTypes.Tavily, name = "Audit", apiKey = "tvly-secret-12345678",
        });

        long generation = 0;
        long adminGeneration = 0;
        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            var config = await ctx.SearchProviderConfigs.SingleAsync(c => c.PublicId == id);
            var state = await ctx.SearchProviderStates.SingleAsync(st => st.ProviderId == config.Id);
            generation = state.Generation;
            adminGeneration = state.AdminGeneration;
            return 0;
        });

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/reset", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/admin/providers/{id}/disable", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/providers/{id}", token, new { name = "Audit 2" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/providers/{id}", token, new { apiKey = "tvly-rotated-87654321" })).StatusCode);

        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            var config = await ctx.SearchProviderConfigs.SingleAsync(c => c.PublicId == id);
            var state = await ctx.SearchProviderStates.SingleAsync(st => st.ProviderId == config.Id);
            // reset + disable + rotasi kunci menaikkan Generation; setiap perubahan konfigurasi menaikkan AdminGeneration.
            Assert.Equal(generation + 3, state.Generation);
            Assert.Equal(adminGeneration + 4, state.AdminGeneration);

            var audits = await ctx.AuditLogs
                .Where(a => a.TenantId == s.TenantId && a.Action.StartsWith("search_provider"))
                .ToListAsync();
            Assert.Contains(audits, a => a.Action == "search_provider.create");
            Assert.Contains(audits, a => a.Action == "search_provider.reset");
            Assert.Contains(audits, a => a.Action == "search_provider.disable");
            Assert.Contains(audits, a => a.Action == "search_provider.update");
            foreach (var audit in audits)
                Assert.DoesNotContain("tvly-", audit.DetailJson ?? "");
            return 0;
        });
    }

    [Fact]
    public async Task Status_reports_budget_exhaustion_and_expired_cooldown_truthfully()
    {
        var (s, token) = await OwnerTenantAsync();
        var budgetId = await CreateAsync(token, new
        {
            type = SearchProviderTypes.Tavily, name = "Anggaran", apiKey = "tvly-secret-12345678",
            monthlyBudget = 10m, costPerSearch = 1m,
        });
        var cooldownId = await CreateAsync(token, new
        {
            type = SearchProviderTypes.Exa, name = "Cooldown", apiKey = "exa-secret-12345678",
        });

        var now = DateTime.UtcNow;
        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            var budgetConfig = await ctx.SearchProviderConfigs.SingleAsync(c => c.PublicId == budgetId);
            var budgetState = await ctx.SearchProviderStates.SingleAsync(st => st.ProviderId == budgetConfig.Id);
            budgetState.UsedThisPeriod = 10m;

            var cooldownConfig = await ctx.SearchProviderConfigs.SingleAsync(c => c.PublicId == cooldownId);
            var cooldownState = await ctx.SearchProviderStates.SingleAsync(st => st.ProviderId == cooldownConfig.Id);
            cooldownState.Status = SearchProviderStatuses.RateLimited;
            cooldownState.UnavailableUntil = now.AddMinutes(-1);
            await ctx.SaveChangesAsync();
            return 0;
        });

        var status = (JsonArray)(await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/providers/status", token)));
        var budget = status.First(i => (string)i!["id"]! == budgetId.ToString())!;
        Assert.Equal(SearchProviderStatuses.Exhausted, (string)budget["status"]!);
        Assert.Equal(0m, (decimal)budget["estimatedRemainingCredits"]!);
        var expectedReset = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        Assert.Equal(expectedReset, budget["unavailableUntil"]!.GetValue<DateTime>());
        Assert.Equal(expectedReset, budget["nextRetryTime"]!.GetValue<DateTime>());

        var cooldown = status.First(i => (string)i!["id"]! == cooldownId.ToString())!;
        Assert.Equal(SearchProviderStatuses.Available, (string)cooldown["status"]!);
        Assert.Null(cooldown["nextRetryTime"]);
    }

    [Fact]
    public async Task Data_plane_search_routes_through_provider_and_clamps_max_results()
    {
        var s = await SetupAsync();
        var (_, _, email) = await NewUserAsync(Roles.Owner, s.TenantId);
        var token = await LoginAsync(email);
        await CreateAsync(token, new
        {
            type = SearchProviderTypes.Tavily, name = "DataPlane", apiKey = "tvly-secret-12345678", costPerSearch = 4m,
        });

        Upstream.Respond = _ => FakeUpstream.Json(200, TavilyOk);
        var unauthenticated = await Client.PostAsJsonAsync("/v1/search", new { query = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/search")
        {
            Content = JsonContent.Create(new { query = "kabar terbaru", maxResults = 50, includeContent = true }),
        };
        request.Headers.Authorization = new("Bearer", s.ApiKey);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(SearchProviderTypes.Tavily, (string)body["provider"]!);
        Assert.Single((JsonArray)body["results"]!);

        // Body yang benar-benar dikirim ke penyedia dibatasi 10 hasil.
        var captured = Assert.Single(Upstream.Requests);
        Assert.Contains("\"max_results\":10", captured.Body);
        Assert.Contains("kabar terbaru", captured.Body);
        Assert.DoesNotContain("tvly-secret-12345678", (await response.Content.ReadAsStringAsync()));

        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            var state = await ctx.SearchProviderStates.Join(
                ctx.SearchProviderConfigs.Where(c => c.Name == "DataPlane"), st => st.ProviderId, c => c.Id,
                (st, c) => st).SingleAsync();
            Assert.Equal(1, state.SuccessCount);
            Assert.Equal(4m, state.UsedThisPeriod);
            return 0;
        });

        // Query terlalu panjang ditolak sebelum menyentuh hub.
        using var tooLong = new HttpRequestMessage(HttpMethod.Post, "/v1/search")
        {
            Content = JsonContent.Create(new { query = new string('q', 501) }),
        };
        tooLong.Headers.Authorization = new("Bearer", s.ApiKey);
        var tooLongResponse = await Client.SendAsync(tooLong);
        Assert.Equal(HttpStatusCode.BadRequest, tooLongResponse.StatusCode);
        Assert.Equal("query_too_long", ErrorCode(await BodyAsync(tooLongResponse)));
    }

    [Fact]
    public async Task Data_plane_search_is_rate_limited_by_tenant_policy()
    {
        var s = await SetupAsync();
        await WithTenantAsync(s.TenantId, (prov, _) =>
            prov.SetPolicyAsync(PolicyScopes.Tenant, s.TenantId, new PolicySpec(RequestsPerMinute: 1), default));

        using var first = new HttpRequestMessage(HttpMethod.Post, "/v1/search")
        {
            Content = JsonContent.Create(new { query = "satu" }),
        };
        first.Headers.Authorization = new("Bearer", s.ApiKey);
        var firstResponse = await Client.SendAsync(first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode); // belum ada penyedia
        Assert.True(firstResponse.Headers.RetryAfter is not null);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/v1/search")
        {
            Content = JsonContent.Create(new { query = "dua" }),
        };
        second.Headers.Authorization = new("Bearer", s.ApiKey);
        var secondResponse = await Client.SendAsync(second);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
        Assert.True(secondResponse.Headers.RetryAfter is not null);
        Assert.Equal("rate_limit_exceeded", ErrorCode(await BodyAsync(secondResponse)));
    }

    [Fact]
    public async Task Data_plane_extract_bounds_input_and_rejects_non_public_urls()
    {
        var s = await SetupAsync();

        async Task<HttpResponseMessage> ExtractAsync(object payload)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/extract")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Authorization = new("Bearer", s.ApiKey);
            return await Client.SendAsync(request);
        }

        var empty = await ExtractAsync(new { urls = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("invalid_urls", ErrorCode(await BodyAsync(empty)));

        var loopback = await ExtractAsync(new { urls = new[] { "http://127.0.0.1:8080/x" } });
        Assert.Equal(HttpStatusCode.BadRequest, loopback.StatusCode);
        Assert.Equal("invalid_url", ErrorCode(await BodyAsync(loopback)));

        var privateIp = await ExtractAsync(new { urls = new[] { "https://192.168.1.10/x" } });
        Assert.Equal(HttpStatusCode.BadRequest, privateIp.StatusCode);

        var badScheme = await ExtractAsync(new { urls = new[] { "ftp://example.com/x" } });
        Assert.Equal(HttpStatusCode.BadRequest, badScheme.StatusCode);

        var tooMany = await ExtractAsync(new { urls = Enumerable.Range(0, 6).Select(i => $"https://example.com/{i}").ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("too_many_urls", ErrorCode(await BodyAsync(tooMany)));

        // URL publik tetap dilanjutkan ke hub (tanpa penyedia -> 503, bukan 400).
        var publicUrl = await ExtractAsync(new { urls = new[] { "https://example.com/artikel" } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, publicUrl.StatusCode);
    }
}
