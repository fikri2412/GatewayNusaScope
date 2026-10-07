using System.Net;
using System.Text.Json.Nodes;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

public class PolicyTests(TestDb db) : GatewayTestBase(db)
{
    // Satu panggilan sukses memakai 10 + 20 = 30 token dan biaya 0.00005 (lihat FakeUpstream.OkBody dan harga 1/2).

    private Task SetPolicyAsync(Scenario s, string scope, long scopeId, PolicySpec spec) =>
        WithTenantAsync(s.TenantId, async (prov, _) => { await prov.SetPolicyAsync(scope, scopeId, spec, default); return 0; });

    private async Task<(long ProjectId, string ApiKey)> AddProjectAndKeyAsync(Scenario s, string name)
    {
        return await WithTenantAsync(s.TenantId, async (prov, _) =>
        {
            var project = await prov.CreateProjectAsync(name, default);
            var key = await prov.CreateApiKeyAsync(project.Id, name, null, null, null, default);
            return (project.Id, key.PlaintextKey);
        });
    }

    private async Task<GatewayResponseView> CallAsync(string apiKey, string body = "")
    {
        var response = await PostChatAsync(apiKey, body.Length == 0 ? Chat() : body);
        return new GatewayResponseView(response.StatusCode, ErrorCode(await BodyAsync(response)), response.Headers.RetryAfter?.Delta);
    }

    private sealed record GatewayResponseView(HttpStatusCode Status, string? Code, TimeSpan? RetryAfter);

    [Fact]
    public async Task Model_allow_list_must_be_satisfied_by_every_scope()
    {
        var s = await SetupAsync();
        await SetPolicyAsync(s, PolicyScopes.Tenant, s.TenantId, new PolicySpec(AllowedModels: ["gpt-test"]));
        await SetPolicyAsync(s, PolicyScopes.Key, s.KeyId, new PolicySpec(AllowedModels: ["something-else"]));

        var denied = await CallAsync(s.ApiKey);
        Assert.Equal((HttpStatusCode.Forbidden, "model_not_allowed"), (denied.Status, denied.Code));
        Assert.Empty(Upstream.Requests);
        await using (var ctx = Db.NewContext(s.TenantId))
            Assert.Equal("model_not_allowed:key", (await ctx.UsageLogs.SingleAsync()).DeniedReason);

        await SetPolicyAsync(s, PolicyScopes.Key, s.KeyId, new PolicySpec(AllowedModels: ["gpt-test"]));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
    }

    [Fact]
    public async Task Output_token_limit_is_the_smallest_of_model_and_policies_and_forced_when_missing()
    {
        var s = await SetupAsync(maxOutput: 1000);
        await SetPolicyAsync(s, PolicyScopes.Tenant, s.TenantId, new PolicySpec(MaxTokensPerRequest: 500));
        await SetPolicyAsync(s, PolicyScopes.Project, s.ProjectId, new PolicySpec(MaxTokensPerRequest: 300));

        await PostChatAsync(s.ApiKey, Chat(extra: ",\"max_tokens\":5000"));
        Assert.Equal(300, (int?)JsonNode.Parse(Upstream.Requests.Last().Body)!["max_tokens"]);

        await PostChatAsync(s.ApiKey, Chat(extra: ",\"max_completion_tokens\":100"));
        var sent = JsonNode.Parse(Upstream.Requests.Last().Body)!;
        Assert.Equal(100, (int?)sent["max_completion_tokens"]); // di bawah batas: tidak diubah
        Assert.Null(sent["max_tokens"]);

        await PostChatAsync(s.ApiKey, Chat()); // klien tidak mengirim batas: kebijakan memaksakan
        Assert.Equal(300, (int?)JsonNode.Parse(Upstream.Requests.Last().Body)!["max_tokens"]);
    }

    [Fact]
    public async Task Without_policies_a_missing_token_limit_is_left_to_the_provider()
    {
        var s = await SetupAsync(maxOutput: 1000);

        await PostChatAsync(s.ApiKey, Chat());

        Assert.Null(JsonNode.Parse(Upstream.Requests.Single().Body)!["max_tokens"]);
    }

    [Fact]
    public async Task Daily_quota_denies_once_the_scope_has_used_it_and_reports_retry_after()
    {
        var s = await SetupAsync();
        await SetPolicyAsync(s, PolicyScopes.Key, s.KeyId, new PolicySpec(DailyTokenQuota: 30));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status); // 0 terpakai < 30
        var denied = await CallAsync(s.ApiKey); // 30 terpakai >= 30

        Assert.Equal(((HttpStatusCode)429, "daily_quota_exceeded"), (denied.Status, denied.Code));
        Assert.InRange(denied.RetryAfter!.Value.TotalSeconds, 1, 86_400);
        Assert.Single(Upstream.Requests);
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.OrderBy(l => l.Id).LastAsync();
        Assert.Equal((UsageStatuses.Denied, "daily_quota_exceeded:key"), (log.Status, log.DeniedReason));
    }

    [Fact]
    public async Task Quotas_are_counted_per_scope_so_a_project_quota_covers_all_its_keys_only()
    {
        var s = await SetupAsync();
        var sameProject = await WithTenantAsync(s.TenantId, async (prov, _) =>
            (await prov.CreateApiKeyAsync(s.ProjectId, "second", null, null, null, default)).PlaintextKey);
        var other = await AddProjectAndKeyAsync(s, "other-project");
        await SetPolicyAsync(s, PolicyScopes.Project, s.ProjectId, new PolicySpec(DailyTokenQuota: 30));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        Assert.Equal("daily_quota_exceeded", (await CallAsync(sameProject)).Code); // key lain, project sama
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(other.ApiKey)).Status);     // project lain tidak terpengaruh
    }

    [Fact]
    public async Task Tenant_quota_covers_every_project_of_the_tenant()
    {
        var s = await SetupAsync();
        var other = await AddProjectAndKeyAsync(s, "other-project");
        await SetPolicyAsync(s, PolicyScopes.Tenant, s.TenantId, new PolicySpec(MonthlyTokenQuota: 30));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        var denied = await CallAsync(other.ApiKey);

        Assert.Equal("monthly_quota_exceeded", denied.Code);
    }

    [Fact]
    public async Task Monthly_budget_denies_when_spend_reaches_the_budget()
    {
        var s = await SetupAsync();
        await SetPolicyAsync(s, PolicyScopes.Project, s.ProjectId, new PolicySpec(MonthlyBudget: 0.00005m));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status); // biaya 0.00005
        Assert.Equal("budget_exceeded", (await CallAsync(s.ApiKey)).Code);
    }

    [Fact]
    public async Task Denied_requests_do_not_consume_quota_and_a_disabled_policy_is_ignored()
    {
        var s = await SetupAsync();
        await SetPolicyAsync(s, PolicyScopes.Key, s.KeyId, new PolicySpec(DailyTokenQuota: 30, Enabled: false));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status); // kebijakan nonaktif
    }

    [Fact]
    public async Task Rate_limit_rejects_the_excess_request_and_keeps_counting_only_accepted_ones()
    {
        var s = await SetupAsync();
        await SetPolicyAsync(s, PolicyScopes.Key, s.KeyId, new PolicySpec(RequestsPerMinute: 2));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        var denied = await CallAsync(s.ApiKey);

        Assert.Equal(((HttpStatusCode)429, "rate_limit_exceeded"), (denied.Status, denied.Code));
        Assert.InRange(denied.RetryAfter!.Value.TotalSeconds, 1, 60);
        Assert.Equal(2, Upstream.Requests.Count);
    }

    [Fact]
    public async Task Plan_token_limit_applies_to_the_whole_tenant()
    {
        var planId = await NewPlanAsync(maxTokensPerMonth: 30);
        var s = await SetupAsync(planId: planId);

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        var denied = await CallAsync(s.ApiKey);

        Assert.Equal(((HttpStatusCode)429, "plan_limit_exceeded"), (denied.Status, denied.Code));
    }

    [Fact]
    public async Task Plan_request_limit_ignores_denied_requests()
    {
        var planId = await NewPlanAsync(maxRequestsPerMonth: 2);
        var s = await SetupAsync(planId: planId);

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        Assert.Equal("model_not_found", (await CallAsync(s.ApiKey, Chat("unknown-model"))).Code); // ditolak, tidak memakai jatah
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(s.ApiKey)).Status);
        Assert.Equal("plan_limit_exceeded", (await CallAsync(s.ApiKey)).Code);
    }

    [Fact]
    public async Task Plan_limits_cap_projects_and_active_keys_and_revoked_keys_free_a_slot()
    {
        var planId = await NewPlanAsync(maxProjects: 1, maxApiKeys: 1);
        var s = await SetupAsync(planId: planId); // sudah memakai 1 project dan 1 key

        await WithTenantAsync(s.TenantId, async (prov, db) =>
        {
            Assert.Equal("plan_limit_reached", (await Assert.ThrowsAsync<GatewayException>(() => prov.CreateProjectAsync("more", default))).Code);
            Assert.Equal("plan_limit_reached", (await Assert.ThrowsAsync<GatewayException>(() => prov.CreateApiKeyAsync(s.ProjectId, "more", null, null, null, default))).Code);

            await db.ApiKeys.Where(k => k.Id == s.KeyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, DateTime.UtcNow));
            await prov.CreateApiKeyAsync(s.ProjectId, "replacement", null, null, null, default);
            return 0;
        });
    }

    [Fact]
    public async Task Policy_input_is_validated_and_scoped_to_the_tenant()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();

        await WithTenantAsync(a.TenantId, async (prov, db) =>
        {
            async Task<string> Code(Func<Task> action) => (await Assert.ThrowsAsync<GatewayException>(action)).Code;

            Assert.Equal("invalid_limit", await Code(() => prov.SetPolicyAsync(PolicyScopes.Key, a.KeyId, new PolicySpec(DailyTokenQuota: 0), default)));
            Assert.Equal("invalid_limit", await Code(() => prov.SetPolicyAsync(PolicyScopes.Key, a.KeyId, new PolicySpec(MonthlyBudget: -1m), default)));
            Assert.Equal("invalid_allowed_models", await Code(() => prov.SetPolicyAsync(PolicyScopes.Key, a.KeyId, new PolicySpec(AllowedModels: []), default)));
            Assert.Equal("invalid_allowed_models", await Code(() => prov.SetPolicyAsync(PolicyScopes.Key, a.KeyId, new PolicySpec(AllowedModels: ["bad alias"]), default)));
            Assert.Equal("invalid_scope", await Code(() => prov.SetPolicyAsync("galaxy", a.KeyId, new PolicySpec(), default)));
            Assert.Equal("not_found", await Code(() => prov.SetPolicyAsync(PolicyScopes.Key, b.KeyId, new PolicySpec(), default))); // key tenant lain
            Assert.Equal("not_found", await Code(() => prov.SetPolicyAsync(PolicyScopes.Tenant, b.TenantId, new PolicySpec(), default)));

            await prov.SetPolicyAsync(PolicyScopes.Key, a.KeyId, new PolicySpec(RequestsPerMinute: 5), default);
            await prov.SetPolicyAsync(PolicyScopes.Key, a.KeyId, new PolicySpec(RequestsPerMinute: 9), default); // upsert
            Assert.Equal(9, (await db.Policies.SingleAsync(p => p.ScopeId == a.KeyId)).RequestsPerMinute);
            return 0;
        });
    }

    private async Task<long> NewPlanAsync(int? maxProjects = null, int? maxApiKeys = null, long? maxRequestsPerMonth = null, long? maxTokensPerMonth = null)
    {
        await using var ctx = Db.NewContext();
        var plan = new Plan
        {
            Name = $"plan-{Guid.NewGuid():N}", MaxProjects = maxProjects, MaxApiKeys = maxApiKeys,
            MaxRequestsPerMonth = maxRequestsPerMonth, MaxTokensPerMonth = maxTokensPerMonth,
        };
        ctx.Plans.Add(plan);
        await ctx.SaveChangesAsync();
        return plan.Id;
    }
}

public class RequestRateLimiterTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(600); // awal sebuah jendela 60 detik

    [Fact]
    public void Excess_requests_are_denied_with_the_remaining_window_as_retry_after()
    {
        var clock = new ManualClock(Epoch);
        var limiter = new RequestRateLimiter(clock);
        (string, long, int)[] key = [("key", 1, 3)];

        Assert.All(Enumerable.Range(0, 3), i => Assert.True(limiter.TryAcquire(key, out _), $"request {i}"));
        clock.Now = Epoch.AddSeconds(30);

        Assert.False(limiter.TryAcquire(key, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);
    }

    [Fact]
    public void Previous_window_decays_linearly_instead_of_resetting_abruptly()
    {
        var clock = new ManualClock(Epoch);
        var limiter = new RequestRateLimiter(clock);
        (string, long, int)[] key = [("key", 1, 3)];
        for (var i = 0; i < 3; i++) limiter.TryAcquire(key, out _);

        clock.Now = Epoch.AddSeconds(60);
        Assert.False(limiter.TryAcquire(key, out _)); // jendela baru, tapi 3 request sebelumnya masih penuh bobotnya

        clock.Now = Epoch.AddSeconds(90);
        Assert.True(limiter.TryAcquire(key, out _));  // 3 x 0.5 + 0 + 1 = 2.5 <= 3
        Assert.False(limiter.TryAcquire(key, out _)); // 1.5 + 1 + 1 = 3.5 > 3

        clock.Now = Epoch.AddSeconds(240); // lebih dari dua jendela berlalu
        Assert.True(limiter.TryAcquire(key, out _));
    }

    [Fact]
    public void A_denial_in_one_scope_does_not_consume_the_other_scopes_allowance()
    {
        var limiter = new RequestRateLimiter(new ManualClock(Epoch));
        (string, long, int)[] tight = [("tenant", 1, 100), ("key", 1, 1)];

        Assert.True(limiter.TryAcquire(tight, out _));
        Assert.False(limiter.TryAcquire(tight, out _)); // key penuh
        Assert.False(limiter.TryAcquire(tight, out _));

        // Tenant hanya terhitung 1x walau ada 3 percobaan (2 ditolak key): tersisa tepat 99 dari jatah 100.
        var accepted = Enumerable.Range(2, 150).Count(i => limiter.TryAcquire([("tenant", 1, 100), ("key", i, 1)], out _));
        Assert.Equal(99, accepted);
    }
}
