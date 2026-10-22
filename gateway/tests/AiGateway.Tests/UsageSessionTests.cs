using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using AiGateway.Core.Domain;
using AiGateway.Core.Security;

namespace AiGateway.Tests;

/// <summary>
/// GB-02: id sesi klien (<c>x-opencode-session</c>) disimpan di <c>usage_logs.session_id</c> dan terlihat lewat log,
/// ekspor CSV, filter <c>sessionId</c>, dan laporan sesi teratas. Tanpa header → null; header tidak valid tetap 400 dan
/// tidak pernah tersimpan; tenant lain tidak terlihat.
/// </summary>
public class UsageSessionTests(TestDb db) : GatewayTestBase(db)
{
    private async Task<string> LoginAsync(string email)
    {
        var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["accessToken"]!;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", token);
        return await Client.SendAsync(request);
    }

    private async Task<(Scenario S, string Token)> OwnerTenantAsync()
    {
        var s = await SetupAsync();
        var (_, _, email) = await NewUserAsync(Roles.Owner, s.TenantId);
        return (s, await LoginAsync(email));
    }

    private static string Iso(DateTime value) => Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    private static string Range() => $"from={Iso(DateTime.UtcNow.AddDays(-1))}&to={Iso(DateTime.UtcNow.AddDays(1))}";

    private Task<HttpResponseMessage> ChatWithSessionAsync(string apiKey, string? session, string model = "gpt-test") =>
        PostChatAsync(apiKey, Chat(model), session is null ? null : r => r.Headers.Add("x-opencode-session", session));

    private async Task<JsonArray> LogItemsAsync(string token, string extra = "")
    {
        var body = await BodyAsync(await GetAsync($"/admin/api/usage/logs?{Range()}{extra}", token));
        return body["items"]!.AsArray();
    }

    private async Task<JsonArray> SessionsAsync(string token, string extra = "")
    {
        var response = await GetAsync($"/admin/api/usage/sessions?{Range()}{extra}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await BodyAsync(response)).AsArray();
    }

    [Fact]
    public async Task A_valid_session_is_stored_and_visible_in_logs_csv_filter_and_the_top_sessions_report()
    {
        var (s, token) = await OwnerTenantAsync();
        await ChatWithSessionAsync(s.ApiKey, "conv-A");
        await ChatWithSessionAsync(s.ApiKey, "conv-A");
        await ChatWithSessionAsync(s.ApiKey, "conv-B");
        await ChatWithSessionAsync(s.ApiKey, null);

        // Log: sesi tampil apa adanya; tanpa header → null.
        var items = await LogItemsAsync(token);
        Assert.Equal(4, items.Count);
        Assert.Equal(2, items.Count(i => (string?)i!["sessionId"] == "conv-A"));
        Assert.Equal(1, items.Count(i => (string?)i!["sessionId"] == "conv-B"));
        Assert.Equal(1, items.Count(i => i!["sessionId"] is null));

        // Filter: persis satu sesi; sesi yang tidak ada → kosong (bukan semua).
        var onlyA = await BodyAsync(await GetAsync($"/admin/api/usage/logs?{Range()}&sessionId=conv-A", token));
        Assert.Equal(2, (int?)onlyA["total"]);
        Assert.All(onlyA["items"]!.AsArray(), i => Assert.Equal("conv-A", (string?)i!["sessionId"]));
        Assert.Equal(0, (int?)(await BodyAsync(await GetAsync($"/admin/api/usage/logs?{Range()}&sessionId=conv-none", token)))["total"]);

        // CSV: kolom session_id di akhir; filter sesi berlaku juga di ekspor.
        var csv = await (await GetAsync($"/admin/api/usage/export?{Range()}", token)).Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        var header = lines[0].Split(',');
        Assert.Equal("session_id", header[^1]);
        Assert.Equal(["", "conv-A", "conv-A", "conv-B"], lines.Skip(1).Select(l => l.Split(',')[^1]).Order().ToArray());
        var csvB = await (await GetAsync($"/admin/api/usage/export?{Range()}&sessionId=conv-B", token)).Content.ReadAsStringAsync();
        var csvBLines = csvB.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, csvBLines.Length); // header + satu baris

        // Laporan sesi: conv-A (2 request) lebih mahal dari conv-B (1 request); request tanpa sesi tidak ikut.
        var sessions = await SessionsAsync(token);
        Assert.Equal(["conv-A", "conv-B"], sessions.Select(r => (string?)r!["sessionId"]).ToArray());
        var a = sessions[0]!;
        var b = sessions[1]!;
        Assert.Equal((2L, 0L, 0L, 0L), ((long?)a["requests"], (long?)a["denied"], (long?)a["errors"], (long?)a["incompleteUsageRequests"]));
        Assert.Equal(1L, (long?)b["requests"]);
        Assert.Equal(20L, (long?)a["inputTokens"]); // 2 × 10 token input dari upstream palsu
        Assert.Equal(40L, (long?)a["outputTokens"]);
        Assert.True((decimal?)b["cost"] > 0m);
        Assert.Equal(2 * (decimal?)b["cost"], (decimal?)a["cost"]);
        Assert.True((DateTime?)a["lastAt"] >= (DateTime?)a["firstAt"]);
    }

    [Fact]
    public async Task Invalid_session_headers_stay_400_and_are_never_stored_as_a_session()
    {
        var (s, token) = await OwnerTenantAsync();
        foreach (var session in new[] { "bad/session", "first,second", new string('a', 201) })
        {
            var response = await ChatWithSessionAsync(s.ApiKey, session);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_session_id", ErrorCode(await BodyAsync(response)));
        }

        var items = await LogItemsAsync(token);
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Null(i!["sessionId"])); // penolakan dicatat, tetapi tanpa sesi yang tidak valid
        Assert.Empty(await SessionsAsync(token));

        // Filter dengan nilai tidak valid ditolak, bukan diam-diam mencocokkan apa pun.
        var bad = await GetAsync($"/admin/api/usage/logs?{Range()}&sessionId={Uri.EscapeDataString("bad/session")}", token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_session_id", ErrorCode(await BodyAsync(bad)));
    }

    [Fact]
    public async Task Denied_and_failed_requests_keep_their_session_and_are_counted_in_the_report()
    {
        var (s, token) = await OwnerTenantAsync();
        await ChatWithSessionAsync(s.ApiKey, "conv-X");
        var missing = await ChatWithSessionAsync(s.ApiKey, "conv-X", "missing-model");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var denied = await BodyAsync(await GetAsync($"/admin/api/usage/logs?{Range()}&status=denied", token));
        Assert.Equal("conv-X", (string?)denied["items"]!.AsArray()[0]!["sessionId"]);

        var row = Assert.Single(await SessionsAsync(token));
        Assert.Equal(("conv-X", 2L, 1L), ((string?)row!["sessionId"], (long?)row["requests"], (long?)row["denied"]));
    }

    [Fact]
    public async Task Sessions_are_tenant_scoped_even_when_two_tenants_use_the_same_id()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        await ChatWithSessionAsync(a.ApiKey, "shared-id");
        await ChatWithSessionAsync(b.ApiKey, "shared-id");
        await ChatWithSessionAsync(b.ApiKey, "shared-id");

        Assert.Equal(1L, (long?)Assert.Single(await SessionsAsync(aToken))!["requests"]);
        Assert.Equal(2L, (long?)Assert.Single(await SessionsAsync(bToken))!["requests"]);
        Assert.Equal(1, (int?)(await BodyAsync(await GetAsync($"/admin/api/usage/logs?{Range()}&sessionId=shared-id", aToken)))["total"]);
    }

    [Fact]
    public async Task Top_sessions_respects_limit_range_and_validation()
    {
        var (s, token) = await OwnerTenantAsync();
        foreach (var session in new[] { "s1", "s2", "s3" }) await ChatWithSessionAsync(s.ApiKey, session);

        Assert.Equal(2, (await SessionsAsync(token, "&limit=2")).Count);
        Assert.Single(await SessionsAsync(token, "&limit=0")); // dibatasi minimal 1
        Assert.Equal(3, (await SessionsAsync(token, "&limit=100000")).Count); // dibatasi maksimal, tidak error

        var future = $"from={Iso(DateTime.UtcNow.AddDays(10))}&to={Iso(DateTime.UtcNow.AddDays(11))}";
        Assert.Empty((await BodyAsync(await GetAsync($"/admin/api/usage/sessions?{future}", token))).AsArray());
        var invalid = await GetAsync($"/admin/api/usage/sessions?from={Iso(DateTime.UtcNow)}&to={Iso(DateTime.UtcNow.AddDays(-1))}", token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task The_migration_adds_a_nullable_session_column_and_a_filtered_index()
    {
        await using var ctx = Db.NewContext();
        var width = await ctx.Database.SqlQuery<int>($"SELECT CAST(COL_LENGTH(N'dbo.usage_logs', N'session_id') AS int) AS [Value]").SingleAsync();
        Assert.Equal(400, width); // nvarchar(200) = 400 byte
        var nullable = await ctx.Database.SqlQuery<int>($"SELECT CAST(is_nullable AS int) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.usage_logs') AND name = N'session_id'").SingleAsync();
        Assert.Equal(1, nullable);
        var filtered = await ctx.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.usage_logs') AND name = N'IX_usage_logs_tenant_id_session_id_created_at' AND has_filter = 1").SingleAsync();
        Assert.Equal(1, filtered);
    }
}

/// <summary>
/// GB-02: skrip idempotent <c>sql/gb02-usage-session-id.sql</c> menaikkan database dari migration sebelumnya, aman diulang,
/// dan tidak mengubah baris lama (sesi tidak diketahui tetap NULL).
/// </summary>
public class UsageSessionScriptTests(TestDb db) : IClassFixture<TestDb>
{
    [Fact]
    public async Task The_checked_in_sql_script_upgrades_a_legacy_database_and_replays_cleanly()
    {
        const string before = "20261014130000_SearchProviderHubAudit";
        const string after = "20261022090000_GB02UsageSessionId";
        await using (var migrate = db.NewContext())
            await migrate.GetService<IMigrator>().MigrateAsync(before);

        var tenant = await db.NewTenantAsync();
        await using var ctx = db.NewContext(tenant.Id);
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO usage_logs
                (tenant_id,project_id,api_key_id,request_id,status,http_status,input_tokens,output_tokens,
                 cached_tokens,reasoning_tokens,cost,currency,latency_ms,attempts,fallback_used,usage_incomplete,created_at)
            VALUES ({tenant.Id},1,1,NEWID(),'ok',200,7,3,0,0,0,'USD',10,1,0,0,SYSUTCDATETIME());
            """);

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "sql", "gb02-usage-session-id.sql");
        var script = await File.ReadAllTextAsync(scriptPath);
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        await ctx.Database.OpenConnectionAsync();
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var batch in batches)
                if (!string.IsNullOrWhiteSpace(batch)) await ctx.Database.ExecuteSqlRawAsync(batch);

            var log = await ctx.UsageLogs.AsNoTracking().SingleAsync();
            Assert.Equal((7, 3, null), (log.InputTokens, log.OutputTokens, log.SessionId)); // baris lama: sesi tidak diketahui
            Assert.Contains(after, await ctx.Database.GetAppliedMigrationsAsync());
        }

        // Skrip menghasilkan skema yang sama dengan migration: kolom dan indeks berfilter ada tepat satu.
        var indexes = await ctx.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.usage_logs') AND name = N'IX_usage_logs_tenant_id_session_id_created_at' AND has_filter = 1").SingleAsync();
        Assert.Equal(1, indexes);
    }
}
