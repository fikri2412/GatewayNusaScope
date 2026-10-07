using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class ProxyTests(TestDb db) : GatewayTestBase(db)
{
    [Fact]
    public async Task Forwards_request_and_records_a_priced_usage_row()
    {
        var s = await SetupAsync();

        var response = await PostChatAsync(s.ApiKey, Chat(extra: ",\"max_tokens\":5000,\"user\":\"end-user-1\""),
            r => r.Headers.Add("X-Gateway-Tags", "a,b"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gpt-test", (string?)(await BodyAsync(response))["model"]); // alias, bukan nama upstream

        var upstream = Assert.Single(Upstream.Requests);
        Assert.Equal("https://up1.test/v1/chat/completions", upstream.Uri.ToString());
        Assert.Equal($"Bearer {UpstreamKey}", upstream.Headers["authorization"]);
        var sent = JsonNode.Parse(upstream.Body)!;
        Assert.Equal("real-model", (string?)sent["model"]);
        Assert.Equal(1000, (int?)sent["max_tokens"]); // dipotong ke batas model

        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal(Guid.Parse(response.Headers.GetValues("X-Request-Id").Single()), log.RequestId);
        Assert.Equal((UsageStatuses.Ok, 10, 20, 4, 5), (log.Status, log.InputTokens, log.OutputTokens, log.CachedTokens, log.ReasoningTokens));
        Assert.Equal(0.00005m, log.Cost); // (10 x 1 + 20 x 2) / 1M; tanpa harga cache, cached ditagih harga input
        Assert.Equal((1m, 2m), (log.InputPriceUsed, log.OutputPriceUsed));
        Assert.Equal(("end-user-1", "a,b", 1, false, "stop"), (log.EndUser, log.Tags, log.Attempts, log.FallbackUsed, log.FinishReason));
        Assert.Equal(s.ModelId, log.ModelId);

        var daily = await ctx.UsageDailies.SingleAsync();
        Assert.Equal((1L, 0L, 0L, 10L, 20L), (daily.Requests, daily.Denied, daily.Errors, daily.InputTokens, daily.OutputTokens));
        Assert.Equal(0.00005m, daily.Cost); // regresi: parameter decimal bawaan memotong ke 0
        Assert.NotNull((await ctx.ApiKeys.SingleAsync()).LastUsedAt);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected_and_not_logged()
    {
        var s = await SetupAsync();
        var tampered = s.ApiKey[..^3] + "xxx";

        foreach (var (auth, code) in new[]
                 {
                     ((string?)null, "missing_api_key"),
                     ("Basic abc", "missing_api_key"),
                     ("Bearer garbage", "invalid_api_key"),
                     ($"Bearer {tampered}", "invalid_api_key"),
                 })
        {
            var response = await PostChatAsync(null, Chat(), r =>
            {
                if (auth is not null) r.Headers.TryAddWithoutValidation("Authorization", auth);
            });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(code, ErrorCode(await BodyAsync(response)));
        }

        Assert.Empty(Upstream.Requests);
        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Empty(await ctx.UsageLogs.ToListAsync()); // pemanggil tak dikenal tidak bisa membanjiri log tenant
    }

    [Fact]
    public Task Revoked_key_is_denied() => AssertDeniedAsync(
        (s, ctx) => ctx.ApiKeys.Where(k => k.Id == s.KeyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, DateTime.UtcNow)),
        HttpStatusCode.Unauthorized, "api_key_revoked");

    [Fact]
    public Task Expired_key_is_denied() => AssertDeniedAsync(
        (s, ctx) => ctx.ApiKeys.Where(k => k.Id == s.KeyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddHours(-1))),
        HttpStatusCode.Unauthorized, "api_key_expired");

    [Fact]
    public Task Suspended_tenant_is_denied() => AssertDeniedAsync(
        (s, ctx) => ctx.Tenants.Where(t => t.Id == s.TenantId).ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, TenantStatuses.Suspended)),
        HttpStatusCode.Forbidden, "tenant_suspended");

    [Fact]
    public Task Suspended_project_is_denied() => AssertDeniedAsync(
        (s, ctx) => ctx.Projects.Where(p => p.Id == s.ProjectId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, ProjectStatuses.Suspended)),
        HttpStatusCode.Forbidden, "project_suspended");

    private async Task AssertDeniedAsync(Func<Scenario, GatewayDbContext, Task> mutate, HttpStatusCode status, string code)
    {
        var s = await SetupAsync();
        await using var ctx = Db.NewContext(s.TenantId);
        await mutate(s, ctx);

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, ErrorCode(await BodyAsync(response)));
        Assert.Empty(Upstream.Requests);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((UsageStatuses.Denied, code, (int)status), (log.Status, log.DeniedReason, log.HttpStatus));
        Assert.Equal(1L, (await ctx.UsageDailies.SingleAsync()).Denied);
    }

    [Fact]
    public async Task Ip_allow_list_is_enforced_per_key()
    {
        var s = await SetupAsync(allowedIps: ["203.0.113.0/24"]);

        var denied = await PostChatAsync(s.ApiKey, Chat(), r => r.Headers.Add("X-Test-Ip", "198.51.100.9"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("ip_not_allowed", ErrorCode(await BodyAsync(denied)));

        var allowed = await PostChatAsync(s.ApiKey, Chat(), r => r.Headers.Add("X-Test-Ip", "203.0.113.7"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Unknown_disabled_and_other_tenants_models_are_not_found()
    {
        var a = await SetupAsync(alias: "only-a");
        var b = await SetupAsync(alias: "only-b");

        var cross = await PostChatAsync(a.ApiKey, Chat("only-b"));
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal("model_not_found", ErrorCode(await BodyAsync(cross)));

        await using var ctx = Db.NewContext(b.TenantId);
        await ctx.Models.Where(m => m.Id == b.ModelId).ExecuteUpdateAsync(u => u.SetProperty(m => m.Enabled, false));
        var disabled = await PostChatAsync(b.ApiKey, Chat("only-b"));
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Empty(Upstream.Requests);
    }

    [Theory]
    [InlineData("not json", "invalid_json")]
    [InlineData("[1]", "invalid_json")]
    [InlineData("""{"messages":[{"role":"user","content":"x"}]}""", "missing_model")]
    [InlineData("""{"model":"gpt-test"}""", "missing_messages")]
    [InlineData("""{"model":"gpt-test","messages":[]}""", "missing_messages")]
    [InlineData("""{"model":"gpt-test","messages":[{"role":"user","content":"x"}],"stream":true}""", "streaming_not_supported")]
    public async Task Invalid_requests_are_rejected_before_reaching_upstream(string body, string code)
    {
        var s = await SetupAsync();

        var response = await PostChatAsync(s.ApiKey, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, ErrorCode(await BodyAsync(response)));
        Assert.Empty(Upstream.Requests);
        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Equal(code, (await ctx.UsageLogs.SingleAsync()).DeniedReason);
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        var s = await SetupAsync();
        var huge = Chat(extra: $",\"pad\":\"{new string('a', 4 * 1024 * 1024 + 10)}\"");

        var response = await PostChatAsync(s.ApiKey, huge);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(Upstream.Requests);
    }

    private async Task<long> AddFallbackRouteAsync(Scenario s)
    {
        return await WithTenantAsync(s.TenantId, async (prov, db) =>
        {
            var second = await prov.CreateCustomProviderAsync("up2", "https://up2.test/v1", "sk-second-5678", null, null, null, default);
            db.ModelRoutes.Add(new ModelRoute { ModelId = s.ModelId, ProviderId = second.Id, UpstreamModel = "real-model-2", Priority = 1 });
            await db.SaveChangesAsync();
            return second.Id;
        });
    }

    [Fact]
    public async Task Falls_back_to_the_next_priority_when_the_first_route_fails()
    {
        var s = await SetupAsync();
        var secondId = await AddFallbackRouteAsync(s);
        Upstream.Respond = c => c.Uri.Host == "up1.test" ? FakeUpstream.Json(500, "{}") : FakeUpstream.Json(200, FakeUpstream.OkBody);

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["up1.test", "up2.test"], Upstream.Requests.Select(r => r.Uri.Host).ToArray()); // priority 0 lebih dulu
        Assert.Equal("real-model-2", (string?)JsonNode.Parse(Upstream.Requests.Last().Body)!["model"]);
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((2, true, secondId, 200), (log.Attempts, log.FallbackUsed, log.ProviderId, log.UpstreamStatus));
    }

    [Fact]
    public async Task Returns_a_gateway_error_when_every_route_fails_and_keeps_retry_after()
    {
        var s = await SetupAsync();
        await AddFallbackRouteAsync(s);
        Upstream.Respond = _ =>
        {
            var r = FakeUpstream.Json(429, "{}");
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        };

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
        Assert.Equal("upstream_rate_limited", ErrorCode(await BodyAsync(response)));
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((UsageStatuses.Error, 2, 429), (log.Status, log.Attempts, log.UpstreamStatus));
        Assert.Equal(1L, (await ctx.UsageDailies.SingleAsync()).Errors);
    }

    [Fact]
    public async Task Upstream_client_errors_pass_through_without_fallback()
    {
        var s = await SetupAsync();
        await AddFallbackRouteAsync(s);
        const string upstreamError = """{"error":{"message":"bad param","type":"invalid_request_error"}}""";
        Upstream.Respond = _ => FakeUpstream.Json(400, upstreamError);

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad param", (string?)(await BodyAsync(response))["error"]!["message"]);
        Assert.Single(Upstream.Requests); // route lain tidak akan memperbaiki permintaan yang salah
    }

    [Fact]
    public async Task Provider_credential_errors_do_not_leak_details_to_the_client()
    {
        var s = await SetupAsync();
        Upstream.Respond = _ => FakeUpstream.Json(401, $$"""{"error":"invalid key {{UpstreamKey}}"}""");

        var response = await PostChatAsync(s.ApiKey, Chat());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("provider_auth_failed", text);
        Assert.DoesNotContain(UpstreamKey, text);
    }

    [Fact]
    public async Task Invalid_or_unreachable_upstream_is_a_bad_gateway()
    {
        var s = await SetupAsync();

        Upstream.Respond = _ => FakeUpstream.Json(200, "not json");
        var invalid = await PostChatAsync(s.ApiKey, Chat());
        Assert.Equal(HttpStatusCode.BadGateway, invalid.StatusCode);
        Assert.Equal("invalid_upstream_response", ErrorCode(await BodyAsync(invalid)));

        Upstream.Respond = _ => throw new HttpRequestException("connection refused");
        var unreachable = await PostChatAsync(s.ApiKey, Chat());
        Assert.Equal(HttpStatusCode.BadGateway, unreachable.StatusCode);
        Assert.Equal("upstream_unreachable", ErrorCode(await BodyAsync(unreachable)));
    }

    [Fact]
    public async Task Provider_keys_are_encrypted_at_rest_and_bound_to_their_tenant()
    {
        var s = await SetupAsync();
        await using var ctx = Db.NewContext(s.TenantId);
        var credential = await ctx.ProviderCredentials.SingleAsync();

        Assert.DoesNotContain(UpstreamKey, credential.ApiKeyEncrypted);
        Assert.Equal("1234", credential.KeyHint);
        var protector = Factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope()
            .ServiceProvider.GetRequiredService<ProviderKeyProtector>();
        Assert.Equal(UpstreamKey, protector.Unprotect(s.TenantId, credential.ApiKeyEncrypted));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(s.TenantId + 1, credential.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Models_endpoint_lists_only_enabled_models_of_the_callers_tenant()
    {
        var a = await SetupAsync(alias: "model-a");
        await SetupAsync(alias: "model-b-other-tenant");
        await WithTenantAsync(a.TenantId, async (prov, db) =>
        {
            var off = await prov.CreateModelAsync(new ModelSpec("model-off", "x"), [new RouteSpec(a.ProviderId, "x")], default);
            await db.Models.Where(m => m.Id == off.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.Enabled, false));
            return 0;
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new("Bearer", a.ApiKey);
        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = ((JsonArray)(await BodyAsync(response))["data"]!).Select(n => (string)n!["id"]!).ToArray();
        Assert.Equal(["model-a"], ids);

        var anonymous = await Client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Custom_auth_header_and_key_rotation_are_used_for_upstream_calls()
    {
        var s = await SetupAsync(authHeader: "x-api-key", authPrefix: "");

        await PostChatAsync(s.ApiKey, Chat());
        var first = Upstream.Requests.Last();
        Assert.Equal(UpstreamKey, first.Headers["x-api-key"]);
        Assert.False(first.Headers.ContainsKey("authorization"));

        await WithTenantAsync(s.TenantId, async (prov, _) => { await prov.RotateProviderKeyAsync(s.ProviderId, "sk-rotated-key-9999", default); return 0; });
        await PostChatAsync(s.ApiKey, Chat());
        Assert.Equal("sk-rotated-key-9999", Upstream.Requests.Last().Headers["x-api-key"]);

        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Equal(1, await ctx.ProviderCredentials.CountAsync(c => c.Status == CredentialStatuses.Active));
        Assert.Equal(1, await ctx.ProviderCredentials.CountAsync(c => c.Status == CredentialStatuses.Disabled));
    }
}
