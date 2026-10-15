using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Laporan status aplikasi klien (6A1) lewat HTTP sungguhan: scope tenant/key, validasi telemetry, upsert satu baris
/// per key+namespace, serta perbandingan configured/applied dan stale di endpoint admin. Laporan bukan pemakaian AI.
/// </summary>
public class AppStatusTests(TestDb db) : GatewayTestBase(db)
{
    private const string LoopKey = "Operator:MaxToolLoops";
    private static readonly string PolicyHash = new('a', 64);
    private static readonly string PromptHash = new('b', 64);

    private async Task<string> TokenAsync(long tenantId, string role)
    {
        var (_, _, email) = await NewUserAsync(role, tenantId);
        using var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["accessToken"]!;
    }

    private async Task SetSettingAsync(string token, string key, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/admin/api/settings/chatbot/{Uri.EscapeDataString(key)}")
        {
            Content = JsonContent.Create(new { value, valueType = "int" }),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> ConfiguredVersionAsync(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/client/api/settings?namespace=chatbot");
        request.Headers.Authorization = new("Bearer", apiKey);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["version"]!;
    }

    private async Task<JsonNode> GetStatusAsync(string token, string query = "?namespace=chatbot")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/app-status" + query);
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    private Task<HttpResponseMessage> PostStatusAsync(string? apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/client/api/app-status") { Content = JsonContent.Create(body) };
        if (apiKey is not null) request.Headers.Authorization = new("Bearer", apiKey);
        return Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostRawAsync(string? apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/client/api/app-status")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (apiKey is not null) request.Headers.Authorization = new("Bearer", apiKey);
        return Client.SendAsync(request);
    }

    /// <summary>Body laporan valid; parameter hanya untuk memutar satu bagian agar kasus penolakan jelas.</summary>
    private static object Body(
        string @namespace = "chatbot",
        string? sourceSettingsVersion = null,
        string syncStatus = "synced",
        Dictionary<string, string>? effectiveValues = null,
        object[]? supportedKeys = null,
        string[]? rejectedKeys = null,
        string[]? ignoredKeys = null,
        string? reason = null,
        long queuePending = 1, long queueProcessing = 2, long queueDone = 3, long queueFailed = 0,
        long jobsPending = 0, long jobsProcessing = 1, long jobsDone = 2, long jobsFailed = 1,
        object[]? activePolicies = null,
        int reportIntervalSeconds = 15,
        string? policyHash = null,
        DateTimeOffset? observedAt = null) =>
        new
        {
            @namespace,
            sourceSettingsVersion,
            policyHash = policyHash ?? PolicyHash,
            promptHash = PromptHash,
            effectiveValues = effectiveValues ?? new Dictionary<string, string> { [LoopKey] = "5" },
            supportedKeys = supportedKeys
                ?? [new { key = LoopKey, valueType = "int", min = 1, max = 20, description = "Batas putaran tool." }],
            syncStatus,
            lastConfigSuccessAt = (DateTimeOffset?)null,
            rejectedKeys = rejectedKeys ?? Array.Empty<string>(),
            ignoredKeys = ignoredKeys ?? Array.Empty<string>(),
            reason,
            queue = new { pending = queuePending, processing = queueProcessing, done = queueDone, failed = queueFailed },
            jobs = new { pending = jobsPending, processing = jobsProcessing, done = jobsDone, failed = jobsFailed },
            activePolicies = activePolicies ?? [new { policyHash = PolicyHash, queueItems = 4L, jobs = 0L }],
            reportIntervalSeconds,
            observedAt = observedAt ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task Report_is_scoped_to_the_calling_key_and_read_back_with_configured_version_and_counts()
    {
        var s = await SetupAsync();
        var admin = await TokenAsync(s.TenantId, Roles.Admin);
        var viewer = await TokenAsync(s.TenantId, Roles.Viewer);
        await SetSettingAsync(admin, LoopKey, "7");
        var version = await ConfiguredVersionAsync(s.ApiKey);

        using var posted = await PostStatusAsync(s.ApiKey, Body(sourceSettingsVersion: version));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.NotNull((DateTime?)(await BodyAsync(posted))["receivedAt"]);

        var body = await GetStatusAsync(viewer);
        Assert.Equal("chatbot", (string?)body["namespace"]);
        Assert.Equal(version, (string?)body["configuredVersion"]);
        var item = Assert.Single(body["items"]!.AsArray());
        Assert.Equal("k", (string?)item!["apiKeyName"]);
        Assert.Equal(version, (string?)item["sourceSettingsVersion"]);
        Assert.Equal("synced", (string?)item["syncStatus"]);
        Assert.False((bool)item["stale"]!);
        Assert.Equal("5", (string?)item["effectiveValues"]![LoopKey]);
        Assert.Equal(3, (long?)item["queue"]!["done"]);
        Assert.Equal(1, (long?)item["jobs"]!["failed"]);
        Assert.Equal(15, (int?)item["reportIntervalSeconds"]);
        Assert.Equal(PolicyHash, (string?)item["activePolicies"]![0]!["policyHash"]);
        Assert.NotNull((DateTime?)item["observedAt"]);

        await using var ctx = Db.NewContext(s.TenantId);
        var publicId = await ctx.ApiKeys.Where(k => k.Id == s.KeyId).Select(k => k.PublicId).SingleAsync();
        Assert.Equal(publicId, (Guid?)item["apiKeyId"]);
        var row = Assert.Single(await ctx.AppStatusReports.AsNoTracking().ToListAsync());
        Assert.Equal((s.TenantId, s.ProjectId, s.KeyId), (row.TenantId, row.ProjectId, row.ApiKeyId));
        // Laporan status bukan pemakaian AI.
        Assert.Equal(0, await ctx.UsageLogs.CountAsync());
    }

    [Fact]
    public async Task Invalid_or_unsafe_reports_are_rejected_without_leaving_rows_or_usage()
    {
        var s = await SetupAsync();

        async Task<string?> BadAsync(object body)
        {
            using var response = await PostStatusAsync(s.ApiKey, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return ErrorCode(await BodyAsync(response));
        }

        // Key yang sama dengan data plane: hilang/salah ditolak dengan format error yang sama.
        using (var missing = await PostStatusAsync(null, Body()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal("missing_api_key", ErrorCode(await BodyAsync(missing)));
        }
        using (var bogus = await PostStatusAsync("gw_zzzzzzzzzz_not-a-real-key", Body()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);
            Assert.Equal("invalid_api_key", ErrorCode(await BodyAsync(bogus)));
        }

        // Telemetry aman: prompt, secret, path, dan key di luar allowlist tidak pernah diterima.
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Prompt:AdditionalInstructions"] = "rahasia" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Storage:RootPath"] = @"C:\storage" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Ai:ApiKey"] = "secret" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Ai:Model"] = "gpt-test" })));
        Assert.Equal("invalid_report", await BadAsync(Body(supportedKeys: [new { key = "Storage:RootPath", valueType = "text" }])));
        // Batas nilai mengikuti AppOptions chatbot.
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { [LoopKey] = "21" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { [LoopKey] = "0" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { [LoopKey] = "abc" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Response:Detail"] = "panjang" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Upload:AllowedExtensions"] = ".png,.exe" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Upload:AllowedExtensions"] = ".png,.PNG" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Document:MaxImagePixels"] = "16000001" })));
        Assert.Equal("invalid_report", await BadAsync(Body(effectiveValues: new() { ["Ai:AttemptTimeoutSeconds"] = "601" })));
        // Bentuk laporan: hash, enum, interval, count, dan panjang teks.
        Assert.Equal("invalid_report", await BadAsync(Body(policyHash: "abc")));
        Assert.Equal("invalid_report", await BadAsync(Body(syncStatus: "ok")));
        Assert.Equal("invalid_report", await BadAsync(Body(reportIntervalSeconds: 0)));
        Assert.Equal("invalid_report", await BadAsync(Body(reportIntervalSeconds: 3601)));
        Assert.Equal("invalid_report", await BadAsync(Body(queuePending: -1)));
        Assert.Equal("invalid_report", await BadAsync(Body(reason: new string('x', 201))));
        Assert.Equal("invalid_report", await BadAsync(Body(activePolicies:
            Enumerable.Range(0, 129).Select(_ => (object)new { policyHash = PolicyHash, queueItems = 1L, jobs = 0L }).ToArray())));
        Assert.Equal("invalid_report", await BadAsync(Body(supportedKeys:
            Enumerable.Range(0, 129).Select(_ => (object)new { key = LoopKey, valueType = "int" }).ToArray())));
        Assert.Equal("invalid_namespace", await BadAsync(Body(@namespace: "Bad Name")));

        // Body bukan objek JSON dan body di atas 64 KB.
        using (var malformed = await PostRawAsync(s.ApiKey, "{"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Equal("invalid_json", ErrorCode(await BodyAsync(malformed)));
        }
        using (var huge = await PostRawAsync(s.ApiKey, JsonSerializer.Serialize(Body(reason: new string('x', 70_000)))))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
            Assert.Equal("request_too_large", ErrorCode(await BodyAsync(huge)));
        }

        // Endpoint admin: tanpa token 401, peran platform (tanpa tenant) 403.
        using (var anonymous = await Client.GetAsync("/admin/api/app-status?namespace=chatbot"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var login = await Client.PostAsJsonAsync("/admin/api/auth/login",
                   new { email = "test-admin@example.test", password = "test-password-123456" }))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/app-status");
            request.Headers.Authorization = new("Bearer", (string?)(await BodyAsync(login))["accessToken"]);
            using var forbidden = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Empty(await ctx.AppStatusReports.AsNoTracking().ToListAsync());
        Assert.Equal(0, await ctx.UsageLogs.CountAsync());
    }

    [Fact]
    public async Task Reports_are_separate_per_key_and_tenant_and_body_cannot_choose_scope()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var secondKey = await WithTenantAsync(a.TenantId, async (provisioning, _) =>
            (await provisioning.CreateApiKeyAsync(a.ProjectId, "k2", null, null, null, default)).PlaintextKey);
        var adminA = await TokenAsync(a.TenantId, Roles.Admin);
        var adminB = await TokenAsync(b.TenantId, Roles.Admin);

        using (var first = await PostStatusAsync(a.ApiKey, Body(effectiveValues: new() { [LoopKey] = "5" })))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var second = await PostStatusAsync(secondKey,
                   Body(syncStatus: "rejected", reason: "sinkron ditolak", effectiveValues: new() { [LoopKey] = "6" })))
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using (var other = await PostStatusAsync(b.ApiKey, Body(syncStatus: "local")))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        var mine = (await GetStatusAsync(adminA))["items"]!.AsArray();
        Assert.Equal(2, mine.Count);
        Assert.Equal(new[] { "k", "k2" },
            mine.Select(i => (string?)i!["apiKeyName"]).OrderBy(name => name).ToArray());
        var rejectedItem = Assert.Single(mine, i => (string?)i!["syncStatus"] == "rejected");
        Assert.Equal("6", (string?)rejectedItem!["effectiveValues"]![LoopKey]);
        // Tenant lain hanya melihat laporannya sendiri.
        Assert.Equal("local", (string?)Assert.Single((await GetStatusAsync(adminB))["items"]!.AsArray())!["syncStatus"]);

        // Laporan berikutnya meng-update baris key itu; tenant/project/key tetap dari key, bukan body.
        var spoof = JsonSerializer.SerializeToNode(Body(effectiveValues: new() { [LoopKey] = "9" }))!.AsObject();
        spoof["tenantId"] = b.TenantId;
        spoof["apiKeyId"] = Guid.NewGuid();
        spoof["projectId"] = b.ProjectId;
        using (var updated = await PostStatusAsync(a.ApiKey, spoof))
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var after = (await GetStatusAsync(adminA))["items"]!.AsArray();
        Assert.Equal(2, after.Count);
        var updatedItem = Assert.Single(after, i => (string?)i!["apiKeyName"] == "k");
        Assert.Equal("9", (string?)updatedItem!["effectiveValues"]![LoopKey]);

        await using var ctx = Db.NewContext(a.TenantId);
        var row = Assert.Single(await ctx.AppStatusReports.AsNoTracking().Where(r => r.ApiKeyId == a.KeyId).ToListAsync());
        Assert.Equal((a.TenantId, a.ProjectId, "chatbot"), (row.TenantId, row.ProjectId, row.Namespace));
    }

    [Fact]
    public async Task Stale_uses_the_server_received_at_and_configured_differs_from_applied()
    {
        var s = await SetupAsync();
        var admin = await TokenAsync(s.TenantId, Roles.Admin);
        await SetSettingAsync(admin, LoopKey, "7");
        var version7 = await ConfiguredVersionAsync(s.ApiKey);

        using (var posted = await PostStatusAsync(s.ApiKey, Body(sourceSettingsVersion: "0000000000000000", reportIntervalSeconds: 1)))
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var body = await GetStatusAsync(admin);
        var item = Assert.Single(body["items"]!.AsArray());
        Assert.Equal(version7, (string?)body["configuredVersion"]);
        Assert.Equal("0000000000000000", (string?)item!["sourceSettingsVersion"]);
        Assert.False((bool)item["stale"]!);

        // Ambang stale = max(60 dtk, 3x interval); laporan 61 dtk lalu dengan interval 1 dtk sudah stale.
        await using (var ctx = Db.NewContext(s.TenantId))
            await ctx.AppStatusReports.ExecuteUpdateAsync(r => r.SetProperty(x => x.ReceivedAt, DateTime.UtcNow.AddSeconds(-61)));
        Assert.True((bool)(await GetStatusAsync(admin))["items"]!.AsArray()[0]!["stale"]!);

        // Interval 3600 dtk menaikkan ambang ke 3 jam: laporan 2 jam lalu masih segar.
        await using (var ctx = Db.NewContext(s.TenantId))
            await ctx.AppStatusReports.ExecuteUpdateAsync(r => r
                .SetProperty(x => x.ReceivedAt, DateTime.UtcNow.AddHours(-2))
                .SetProperty(x => x.ReportIntervalSeconds, 3600));
        Assert.False((bool)(await GetStatusAsync(admin))["items"]!.AsArray()[0]!["stale"]!);

        // Mengubah pengaturan mengubah configured, bukan applied: aplikasi menyesuaikan pada siklus berikutnya.
        await SetSettingAsync(admin, LoopKey, "8");
        var version8 = await ConfiguredVersionAsync(s.ApiKey);
        Assert.NotEqual(version7, version8);
        var after = await GetStatusAsync(admin);
        Assert.Equal(version8, (string?)after["configuredVersion"]);
        Assert.Equal("0000000000000000", (string?)after["items"]!.AsArray()[0]!["sourceSettingsVersion"]);
    }
}
