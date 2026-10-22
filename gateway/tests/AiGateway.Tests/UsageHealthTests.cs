using System.Diagnostics;
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
/// GB-03: kesehatan model/provider dihitung di SQL dari <c>usage_logs</c>. Nilai dicocokkan dengan hitungan manual pada data
/// yang dibuat test; jendela kosong berarti "tidak ada data"; request yang ditolak, dibatalkan klien, di luar jendela, atau milik
/// tenant lain tidak ikut; waktu query pada data besar diukur.
/// </summary>
public class UsageHealthTests(TestDb db, Xunit.Abstractions.ITestOutputHelper output) : GatewayTestBase(db)
{
    private async Task<string> LoginAsync(string email)
    {
        var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["accessToken"]!;
    }

    private async Task<(Scenario S, string Token)> OwnerTenantAsync(string role = Roles.Owner)
    {
        var s = await SetupAsync();
        var (_, _, email) = await NewUserAsync(role, s.TenantId);
        return (s, await LoginAsync(email));
    }

    private async Task<HttpResponseMessage> HealthAsync(string? token, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/api/usage/health{query}");
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        return await Client.SendAsync(request);
    }

    private async Task<JsonNode> HealthBodyAsync(string token, string query = "")
    {
        var response = await HealthAsync(token, query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    private static UsageLog Row(
        Scenario s, long modelId, long providerId, string status = UsageStatuses.Ok, int http = 200, string? reason = null,
        int attempts = 1, bool fallback = false, int latency = 100, int? ttfb = 10, TimeSpan? age = null) => new()
    {
        TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = modelId, ProviderId = providerId,
        Status = status, HttpStatus = http, DeniedReason = reason, Attempts = attempts, FallbackUsed = fallback,
        LatencyMs = latency, TtfbMs = ttfb, UsageIncomplete = false, CreatedAt = DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(5)),
    };

    private async Task AddRowsAsync(long tenantId, IEnumerable<UsageLog> rows)
    {
        await using var ctx = Db.NewContext(tenantId);
        ctx.UsageLogs.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    private static long L(JsonNode? node) => node!.GetValue<long>();

    private static double D(JsonNode? node) => node!.GetValue<double>();

    private static JsonNode ByLabel(JsonNode body, string list, string label) =>
        body[list]!.AsArray().Single(r => (string?)r!["label"] == label)!;

    [Fact]
    public async Task Health_matches_a_manual_calculation_per_model_and_per_provider()
    {
        var (s, token) = await OwnerTenantAsync();
        var (modelB, provider2) = await WithTenantAsync(s.TenantId, async (prov, _) =>
        {
            var model = await prov.CreateModelAsync(new ModelSpec("model-b", "real-b", 1000, 1m, 2m), [new RouteSpec(s.ProviderId, "real-b")], default);
            var second = await prov.CreateCustomProviderAsync("p2", "https://p2.test/v1", "sk-p2-1234", null, null, null, default);
            return (model.Id, second.Id);
        });
        var other = await SetupAsync(); // tenant lain: tidak boleh ikut terhitung
        var rows = new List<UsageLog>();
        // Model A di provider 1: 10 sukses (latensi 100..1000, TTFB 10..100).
        for (var i = 1; i <= 10; i++) rows.Add(Row(s, s.ModelId, s.ProviderId, latency: i * 100, ttfb: i * 10));
        // Dua sukses lewat fallback, dilayani provider 2 (latensi 1100/1200, TTFB 110/120).
        rows.Add(Row(s, s.ModelId, provider2, attempts: 2, fallback: true, latency: 1100, ttfb: 110));
        rows.Add(Row(s, s.ModelId, provider2, attempts: 2, fallback: true, latency: 1200, ttfb: 120));
        // Tiga kegagalan provider: 502, 504, dan stream yang timeout setelah commit (HTTP 200 tetapi status error).
        rows.Add(Row(s, s.ModelId, s.ProviderId, UsageStatuses.Error, 502, "upstream_failed", ttfb: null));
        rows.Add(Row(s, s.ModelId, s.ProviderId, UsageStatuses.Error, 504, "upstream_failed", ttfb: null));
        rows.Add(Row(s, s.ModelId, s.ProviderId, UsageStatuses.Error, 200, "upstream_stream_timeout"));
        // 4xx dari request klien yang diteruskan apa adanya: provider menjawab, bukan kegagalan provider.
        rows.Add(Row(s, s.ModelId, s.ProviderId, UsageStatuses.Error, 400, "upstream_failed"));
        // Tidak ikut statistik upstream: pembatalan klien, penolakan kebijakan (tanpa percobaan provider), dan di luar jendela.
        rows.Add(Row(s, s.ModelId, s.ProviderId, UsageStatuses.Error, 499, null));
        rows.Add(Row(s, s.ModelId, s.ProviderId, UsageStatuses.Denied, 429, "rate_limit_exceeded", attempts: 0));
        rows.Add(Row(s, s.ModelId, s.ProviderId, age: TimeSpan.FromDays(3)));
        // Model B: 30 sukses (tepat di ambang sampel).
        for (var i = 0; i < 30; i++) rows.Add(Row(s, modelB, s.ProviderId, latency: 50, ttfb: 5));
        await AddRowsAsync(s.TenantId, rows);
        await AddRowsAsync(other.TenantId, Enumerable.Range(0, 5).Select(_ => Row(other, other.ModelId, other.ProviderId, UsageStatuses.Error, 502, "upstream_failed")));

        var body = await HealthBodyAsync(token, "?window=24h");
        Assert.Equal("24h", (string?)body["window"]);
        Assert.Equal(30, (int?)body["minSample"]);

        // Model: urut menurut jumlah request (B lalu A).
        Assert.Equal(["model-b", "gpt-test"], body["models"]!.AsArray().Select(r => (string?)r!["label"]).ToArray());
        var a = ByLabel(body, "models", "gpt-test");
        Assert.Equal(16L, L(a["requests"]));
        Assert.Equal(12L, L(a["succeeded"]));
        Assert.Equal(3L, L(a["failures"]));
        Assert.Equal(3.0 / 16.0, D(a["errorRate"]), 6);
        Assert.Equal(2.0 / 12.0, D(a["fallbackRate"]), 6);
        Assert.Equal(650, (int?)a["latencyP50Ms"]); // 12 nilai 100..1200, interpolasi PERCENTILE_CONT
        Assert.Equal(1145, (int?)a["latencyP95Ms"]);
        Assert.Equal(65, (int?)a["ttfbP50Ms"]);
        Assert.InRange((int)a["ttfbP95Ms"]!, 114, 115); // 114,5
        Assert.True((bool?)a["smallSample"]);
        Assert.Equal((await PublicIdAsync(s.TenantId, s.ModelId, model: true)).ToString(), (string?)a["id"]);

        var b = ByLabel(body, "models", "model-b");
        Assert.Equal(30L, L(b["requests"]));
        Assert.Equal(30L, L(b["succeeded"]));
        Assert.Equal(0L, L(b["failures"]));
        Assert.Equal(0.0, D(b["errorRate"]));
        Assert.False((bool?)b["smallSample"]);
        Assert.Equal(50, (int?)b["latencyP50Ms"]);
        Assert.Equal(5, (int?)b["ttfbP50Ms"]);

        // Provider: provider 1 melayani B dan sebagian A; provider 2 hanya dua jawaban fallback.
        var p1 = ByLabel(body, "providers", "up");
        Assert.Equal(44L, L(p1["requests"]));
        Assert.Equal(40L, L(p1["succeeded"]));
        Assert.Equal(3L, L(p1["failures"]));
        Assert.Equal(0.0, D(p1["fallbackRate"]));
        Assert.Equal(50, (int?)p1["latencyP50Ms"]);
        Assert.Equal(805, (int?)p1["latencyP95Ms"]);
        Assert.False((bool?)p1["smallSample"]);
        var p2 = ByLabel(body, "providers", "p2");
        Assert.Equal(2L, L(p2["requests"]));
        Assert.Equal(2L, L(p2["succeeded"]));
        Assert.Equal(0L, L(p2["failures"]));
        Assert.Equal(1.0, D(p2["fallbackRate"])); // semua jawabannya fallback
        Assert.Equal(1150, (int?)p2["latencyP50Ms"]);
        Assert.True((bool?)p2["smallSample"]);

        // Jendela 7 hari menyertakan baris tiga hari lalu (satu request sukses tambahan di model A / provider 1).
        var week = await HealthBodyAsync(token, "?window=7d");
        Assert.Equal(17L, L(ByLabel(week, "models", "gpt-test")["requests"]));
        Assert.Equal(45L, L(ByLabel(week, "providers", "up")["requests"]));
        // Jendela 1 jam masih menyertakan baris lima menit lalu.
        Assert.Equal(16L, L(ByLabel(await HealthBodyAsync(token, "?window=1h"), "models", "gpt-test")["requests"]));
    }

    private async Task<Guid> PublicIdAsync(long tenantId, long id, bool model)
    {
        await using var ctx = Db.NewContext(tenantId);
        return model
            ? await ctx.Models.IgnoreQueryFilters().Where(m => m.Id == id).Select(m => m.PublicId).SingleAsync()
            : await ctx.Providers.IgnoreQueryFilters().Where(p => p.Id == id).Select(p => p.PublicId).SingleAsync();
    }

    [Fact]
    public async Task An_empty_window_reports_no_data_instead_of_zero_or_perfect_rates()
    {
        var (s, token) = await OwnerTenantAsync();
        var empty = await HealthBodyAsync(token);
        Assert.Equal("24h", (string?)empty["window"]); // jendela bawaan
        Assert.Empty(empty["models"]!.AsArray());
        Assert.Empty(empty["providers"]!.AsArray());
        Assert.True((DateTime?)empty["to"] > (DateTime?)empty["from"]);

        // Hanya baris lama (di luar jendela) dan baris yang ditolak: tetap kosong, bukan "100% sehat".
        await AddRowsAsync(s.TenantId,
        [
            Row(s, s.ModelId, s.ProviderId, age: TimeSpan.FromDays(3)),
            Row(s, s.ModelId, s.ProviderId, UsageStatuses.Denied, 401, "api_key_revoked", attempts: 0),
            Row(s, s.ModelId, s.ProviderId, UsageStatuses.Error, 499, null),
        ]);
        var still = await HealthBodyAsync(token, "?window=24h");
        Assert.Empty(still["models"]!.AsArray());
        Assert.Empty(still["providers"]!.AsArray());
    }

    [Fact]
    public async Task Tenants_only_see_their_own_health_and_viewers_can_read()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        await AddRowsAsync(a.TenantId, Enumerable.Range(0, 3).Select(_ => Row(a, a.ModelId, a.ProviderId)));
        await AddRowsAsync(b.TenantId, Enumerable.Range(0, 7).Select(_ => Row(b, b.ModelId, b.ProviderId, UsageStatuses.Error, 502, "upstream_failed")));

        var aModel = (await HealthBodyAsync(aToken))["models"]!.AsArray().Single()!;
        Assert.Equal(3L, L(aModel["requests"]));
        Assert.Equal(0L, L(aModel["failures"]));
        var bModel = (await HealthBodyAsync(bToken))["models"]!.AsArray().Single()!;
        Assert.Equal(7L, L(bModel["requests"]));
        Assert.Equal(7L, L(bModel["failures"]));

        var (_, _, viewerEmail) = await NewUserAsync(Roles.Viewer, a.TenantId);
        Assert.Equal(HttpStatusCode.OK, (await HealthAsync(await LoginAsync(viewerEmail))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HealthAsync(null)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_window_is_rejected_and_the_maximum_is_seven_days()
    {
        var (_, token) = await OwnerTenantAsync();
        foreach (var window in new[] { "30d", "8d", "0h", "week", "168h" })
        {
            var response = await HealthAsync(token, $"?window={window}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_window", ErrorCode(await BodyAsync(response)));
        }
        foreach (var window in new[] { "1h", "6h", "24h", "7d" })
            Assert.Equal(window, (string?)(await HealthBodyAsync(token, $"?window={window}"))["window"]);
    }

    [Fact]
    public async Task Query_time_on_a_large_log_is_measured()
    {
        var (s, token) = await OwnerTenantAsync();
        const int rowCount = 100_000;
        await using (var ctx = Db.NewContext(s.TenantId))
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO usage_logs
                    (tenant_id,project_id,api_key_id,model_id,provider_id,request_id,status,http_status,input_tokens,output_tokens,
                     cached_tokens,reasoning_tokens,cost,currency,latency_ms,ttfb_ms,attempts,fallback_used,usage_incomplete,created_at)
                SELECT TOP ({rowCount}) {s.TenantId},{s.ProjectId},{s.KeyId},{s.ModelId},{s.ProviderId},NEWID(),'ok',200,10,20,0,0,0,'USD',
                       ABS(CHECKSUM(NEWID())) % 3000, ABS(CHECKSUM(NEWID())) % 500, 1, 0, 0,
                       DATEADD(SECOND, -(ABS(CHECKSUM(NEWID())) % 80000), SYSUTCDATETIME())
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
                """);
        }

        var stopwatch = Stopwatch.StartNew();
        var body = await HealthBodyAsync(token, "?window=24h");
        stopwatch.Stop();
        output.WriteLine($"GB-03: health 24h atas {rowCount.ToString("N0", CultureInfo.InvariantCulture)} baris usage_logs = {stopwatch.ElapsedMilliseconds} ms");

        Assert.Equal(rowCount, (long?)body["models"]!.AsArray().Single()!["requests"]);
        Assert.Equal(rowCount, (long?)body["providers"]!.AsArray().Single()!["requests"]);
        // Batas longgar penjaga regresi (mis. kembali memuat log ke memori atau scan per baris); angka sebenarnya ada di output.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"query kesehatan terlalu lambat: {stopwatch.ElapsedMilliseconds} ms");
    }
}
