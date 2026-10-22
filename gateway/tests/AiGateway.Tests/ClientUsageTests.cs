using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// GB-04: <c>GET /client/api/usage</c> — pemakaian dan sisa kuota efektif untuk pemanggil API key. Key hanya melihat datanya
/// sendiri, angkanya sama dengan laporan admin, penolakan sama dengan data plane, dan tidak ada yang dicatat ke <c>usage_logs</c>.
/// Satu panggilan chat sukses = 30 token (10 + 20) dan biaya 0.00005 (lihat <see cref="FakeUpstream.OkBody"/>).
/// </summary>
public class ClientUsageTests(TestDb db) : GatewayTestBase(db)
{
    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<HttpResponseMessage> UsageAsync(string? apiKey, string query = "", Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/client/api/usage{query}");
        if (apiKey is not null) request.Headers.Authorization = new("Bearer", apiKey);
        tweak?.Invoke(request);
        return await Client.SendAsync(request);
    }

    private async Task<JsonNode> UsageBodyAsync(string apiKey, string query = "")
    {
        var response = await UsageAsync(apiKey, query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    private Task SetPolicyAsync(Scenario s, string scope, long scopeId, PolicySpec spec) =>
        WithTenantAsync(s.TenantId, async (prov, _) => { await prov.SetPolicyAsync(scope, scopeId, spec, default); return 0; });

    private async Task<(long KeyId, string ApiKey)> AddKeyAsync(Scenario s, string name) =>
        await WithTenantAsync(s.TenantId, async (prov, _) =>
        {
            var key = await prov.CreateApiKeyAsync(s.ProjectId, name, null, null, null, default);
            return (key.Entity.Id, key.PlaintextKey);
        });

    private async Task<long> NewPlanAsync(long? maxRequestsPerMonth = null, long? maxTokensPerMonth = null)
    {
        await using var ctx = Db.NewContext();
        var plan = new Plan { Name = $"plan-{Guid.NewGuid():N}", MaxRequestsPerMonth = maxRequestsPerMonth, MaxTokensPerMonth = maxTokensPerMonth };
        ctx.Plans.Add(plan);
        await ctx.SaveChangesAsync();
        return plan.Id;
    }

    private async Task<int> UsageLogCountAsync(long tenantId)
    {
        await using var ctx = Db.NewContext(tenantId);
        return await ctx.UsageLogs.CountAsync();
    }

    private static long L(JsonNode? node) => node!.GetValue<long>();

    [Fact]
    public async Task A_key_sees_only_its_own_usage_and_the_numbers_match_the_admin_summary()
    {
        var s = await SetupAsync();
        var (_, otherKey) = await AddKeyAsync(s, "other-key"); // project yang sama, key lain
        await PostChatAsync(s.ApiKey, Chat());
        await PostChatAsync(s.ApiKey, Chat());
        await PostChatAsync(s.ApiKey, Chat("missing-model")); // ditolak
        await PostChatAsync(otherKey, Chat());

        var mine = await UsageBodyAsync(s.ApiKey);
        var days = mine["days"]!.AsArray();
        Assert.Equal(7, days.Count); // bawaan tujuh hari terakhir, termasuk hari tanpa pemakaian
        Assert.Equal(Today(), (string?)days[^1]!["day"]);
        Assert.Equal(Today(), (string?)mine["to"]);
        var total = mine["total"]!;
        Assert.Equal(3L, L(total["requests"]));
        Assert.Equal(1L, L(total["denied"]));
        Assert.Equal(20L, L(total["inputTokens"]));
        Assert.Equal(40L, L(total["outputTokens"]));
        Assert.Equal(0.0001m, (decimal?)total["cost"]);
        Assert.Equal(0L, L(total["incompleteUsageRequests"]));
        Assert.Equal(3L, L(days[^1]!["requests"]));
        Assert.All(days.Take(6), d => Assert.Equal(0L, L(d!["requests"])));

        // Key lain di project yang sama hanya melihat miliknya sendiri.
        var theirs = await UsageBodyAsync(otherKey);
        Assert.Equal(1L, L(theirs["total"]!["requests"]));
        Assert.Equal(10L, L(theirs["total"]!["inputTokens"]));

        // Angka sama dengan laporan admin (ringkasan per key).
        var (_, _, email) = await NewUserAsync(Roles.Owner, s.TenantId);
        var login = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = StrongPassword });
        var token = (string)(await BodyAsync(login))["accessToken"]!;
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/api/usage/summary?from={Today()}&to={Today()}&groupBy=key");
        adminRequest.Headers.Authorization = new("Bearer", token);
        var summary = (await BodyAsync(await Client.SendAsync(adminRequest))).AsArray();
        var adminRow = summary.Single(r => (string?)r!["label"] == "k")!;
        Assert.Equal(L(total["requests"]), L(adminRow["requests"]));
        Assert.Equal(L(total["inputTokens"]), L(adminRow["inputTokens"]));
        Assert.Equal(L(total["outputTokens"]), L(adminRow["outputTokens"]));
        Assert.Equal((decimal?)total["cost"], (decimal?)adminRow["cost"]);
    }

    [Fact]
    public async Task Reading_usage_writes_nothing_to_usage_logs_and_rejected_keys_get_the_data_plane_errors()
    {
        var s = await SetupAsync();
        await PostChatAsync(s.ApiKey, Chat());
        var before = await UsageLogCountAsync(s.TenantId);

        Assert.Equal(HttpStatusCode.OK, (await UsageAsync(s.ApiKey)).StatusCode);
        var missing = await UsageAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("missing_api_key", ErrorCode(await BodyAsync(missing)));
        var wrong = await UsageAsync("gw_not-a-real-key");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        await using (var ctx = Db.NewContext(s.TenantId))
            await ctx.ApiKeys.Where(k => k.Id == s.KeyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, DateTime.UtcNow));
        var revoked = await UsageAsync(s.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("api_key_revoked", ErrorCode(await BodyAsync(revoked)));

        var expired = await SetupAsync();
        await using (var ctx = Db.NewContext(expired.TenantId))
            await ctx.ApiKeys.Where(k => k.Id == expired.KeyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        Assert.Equal("api_key_expired", ErrorCode(await BodyAsync(await UsageAsync(expired.ApiKey))));

        var restricted = await SetupAsync(allowedIps: ["10.9.9.9"]);
        var blocked = await UsageAsync(restricted.ApiKey);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("ip_not_allowed", ErrorCode(await BodyAsync(blocked)));
        Assert.Equal(HttpStatusCode.OK, (await UsageAsync(restricted.ApiKey, tweak: r => r.Headers.Add("X-Test-Ip", "10.9.9.9"))).StatusCode);

        // Hanya request chat awal yang tercatat; pembacaan dan semua penolakan di atas tidak menulis log pemakaian.
        Assert.Equal(before, await UsageLogCountAsync(s.TenantId));
        Assert.Equal(0, await UsageLogCountAsync(expired.TenantId));
        Assert.Equal(0, await UsageLogCountAsync(restricted.TenantId));
    }

    [Fact]
    public async Task The_range_is_validated_limited_to_31_days_and_defaults_to_seven()
    {
        var s = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Equal(7, (await UsageBodyAsync(s.ApiKey))["days"]!.AsArray().Count);
        var thirtyOne = await UsageBodyAsync(s.ApiKey, $"?from={today.AddDays(-30):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Equal(31, thirtyOne["days"]!.AsArray().Count);
        var single = await UsageBodyAsync(s.ApiKey, $"?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Single(single["days"]!.AsArray());
        // Hanya 'from' diberikan: sampai hari ini (UTC).
        var fromOnly = await UsageBodyAsync(s.ApiKey, $"?from={today.AddDays(-2):yyyy-MM-dd}");
        Assert.Equal(3, fromOnly["days"]!.AsArray().Count);

        async Task<string?> CodeAsync(string query)
        {
            var response = await UsageAsync(s.ApiKey, query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return ErrorCode(await BodyAsync(response));
        }
        Assert.Equal("range_too_large", await CodeAsync($"?from={today.AddDays(-31):yyyy-MM-dd}&to={today:yyyy-MM-dd}")); // 32 hari
        Assert.Equal("invalid_range", await CodeAsync($"?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}"));
        Assert.Equal("invalid_range", await CodeAsync("?from=2026-13-40&to=2026-13-41"));
        Assert.Equal("invalid_range", await CodeAsync("?from=2026/10/01&to=2026/10/02"));
        Assert.Equal("invalid_range", await CodeAsync("?from=bukan-tanggal"));
    }

    [Fact]
    public async Task Quota_reports_the_tightest_scope_for_each_kind_of_limit()
    {
        var planId = await NewPlanAsync(maxRequestsPerMonth: 1000, maxTokensPerMonth: 500);
        var s = await SetupAsync(planId: planId);
        await SetPolicyAsync(s, PolicyScopes.Tenant, s.TenantId, new PolicySpec(
            MaxTokensPerRequest: 500, RequestsPerMinute: 60, DailyTokenQuota: 1000, MonthlyTokenQuota: 10_000, MonthlyBudget: 5m));
        await SetPolicyAsync(s, PolicyScopes.Project, s.ProjectId, new PolicySpec(DailyTokenQuota: 500));
        await SetPolicyAsync(s, PolicyScopes.Key, s.KeyId, new PolicySpec(
            MaxTokensPerRequest: 300, RequestsPerMinute: 10, DailyTokenQuota: 100, MonthlyBudget: 0.01m));
        await PostChatAsync(s.ApiKey, Chat());
        await PostChatAsync(s.ApiKey, Chat()); // 60 token, biaya 0.0001

        var quota = (await UsageBodyAsync(s.ApiKey))["quota"]!;

        var daily = quota["dailyTokens"]!;
        Assert.Equal((100L, 60L, 40L, "key"), (L(daily["limit"]), L(daily["used"]), L(daily["remaining"]), (string?)daily["limitedBy"]));
        var nextMidnight = DateTime.UtcNow.Date.AddDays(1);
        Assert.Equal(nextMidnight, ((DateTime?)daily["resetsAt"])!.Value.ToUniversalTime());

        var monthly = quota["monthlyTokens"]!; // plan 500 lebih ketat dari kuota tenant 10.000
        Assert.Equal((500L, 60L, 440L, "plan"), (L(monthly["limit"]), L(monthly["used"]), L(monthly["remaining"]), (string?)monthly["limitedBy"]));
        var firstOfNextMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        Assert.Equal(firstOfNextMonth, ((DateTime?)monthly["resetsAt"])!.Value.ToUniversalTime());

        var requests = quota["monthlyRequests"]!;
        Assert.Equal((1000L, 2L, 998L, "plan"), (L(requests["limit"]), L(requests["used"]), L(requests["remaining"]), (string?)requests["limitedBy"]));

        var budget = quota["monthlyBudget"]!; // 0.01 (key) lebih ketat dari 5 (tenant)
        Assert.Equal("key", (string?)budget["limitedBy"]);
        Assert.Equal(0.01m, (decimal?)budget["limit"]);
        Assert.Equal(0.0001m, (decimal?)budget["used"]);
        Assert.Equal(0.0099m, (decimal?)budget["remaining"]);

        Assert.Equal((10L, "key"), (L(quota["requestsPerMinute"]!["limit"]), (string?)quota["requestsPerMinute"]!["limitedBy"]));
        Assert.Equal(300, (int?)quota["maxTokensPerRequest"]);
    }

    [Fact]
    public async Task Without_any_limits_every_quota_field_is_null()
    {
        var s = await SetupAsync();
        await PostChatAsync(s.ApiKey, Chat());

        var quota = (await UsageBodyAsync(s.ApiKey))["quota"]!;
        foreach (var field in new[] { "dailyTokens", "monthlyTokens", "monthlyRequests", "monthlyBudget", "requestsPerMinute", "maxTokensPerRequest" })
            Assert.Null(quota[field]);
    }

    [Fact]
    public async Task An_exhausted_quota_shows_zero_remaining_never_negative()
    {
        var s = await SetupAsync();
        await SetPolicyAsync(s, PolicyScopes.Key, s.KeyId, new PolicySpec(DailyTokenQuota: 30));
        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(s.ApiKey, Chat())).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await PostChatAsync(s.ApiKey, Chat())).StatusCode); // batas sudah tercapai

        var daily = (await UsageBodyAsync(s.ApiKey))["quota"]!["dailyTokens"]!;
        Assert.Equal((30L, 30L, 0L), (L(daily["limit"]), L(daily["used"]), L(daily["remaining"])));
    }
}
