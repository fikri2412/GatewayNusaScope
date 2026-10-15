using System.Net;
using System.Net.Http.Json;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Pengaturan aplikasi per tenant (G6) lewat HTTP sungguhan: CRUD admin dengan validasi, peran, isolasi lintas tenant,
/// audit, dan pembacaan klien dengan API key (ETag, penolakan, tanpa jejak pemakaian AI).
/// </summary>
public class SettingsTests(TestDb db) : GatewayTestBase(db)
{
    private const string SeedAdminEmail = "test-admin@example.test";
    private const string SeedAdminPassword = "test-password-123456";
    private const string MaxOperators = "Dispatcher:MaxConcurrentOperators";

    private async Task<string> LoginAsync(string email, string password = StrongPassword)
    {
        using var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["accessToken"]!;
    }

    private async Task<string> TenantTokenAsync(long tenantId, string role)
    {
        var (_, _, email) = await NewUserAsync(role, tenantId);
        return await LoginAsync(email);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PutAsync(string token, string ns, string key, string value, string type, string? description = null) =>
        SendAsync(HttpMethod.Put, $"/admin/api/settings/{ns}/{Uri.EscapeDataString(key)}", token, new { value, valueType = type, description });

    private async Task<HttpResponseMessage> ClientGetAsync(string? apiKey, string query = "?namespace=chatbot", string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/client/api/settings" + query);
        if (apiKey is not null) request.Headers.Authorization = new("Bearer", apiKey);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await Client.SendAsync(request);
    }

    [Fact]
    public async Task Admin_creates_updates_lists_and_deletes_settings_and_every_change_is_audited()
    {
        var s = await SetupAsync();
        var admin = await TenantTokenAsync(s.TenantId, Roles.Admin);

        using var created = await PutAsync(admin, "chatbot", MaxOperators, " 4 ", SettingValueTypes.Int, "Batas operator");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var first = await BodyAsync(created);
        Assert.Equal(("chatbot", MaxOperators, "4", "int", "Batas operator"),
            ((string?)first["namespace"], (string?)first["key"], (string?)first["value"], (string?)first["valueType"], (string?)first["description"]));

        using var updated = await PutAsync(admin, "chatbot", MaxOperators, "6", SettingValueTypes.Int);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var flag = await PutAsync(admin, "chatbot", "Feature.Echo", "TRUE", SettingValueTypes.Bool);
        Assert.Equal("true", (string?)(await BodyAsync(flag))["value"]);
        using var other = await PutAsync(admin, "other", "Note", "bebas teks", SettingValueTypes.Text);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        // Satu key = satu baris (put kedua mengubah, bukan menambah); deskripsi yang dikosongkan terhapus.
        var listed = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/settings?namespace=chatbot", admin))).AsArray();
        Assert.Equal([MaxOperators, "Feature.Echo"], listed.Select(i => (string)i!["key"]!).ToArray());
        var operators = Assert.Single(listed, i => (string?)i!["key"] == MaxOperators);
        Assert.Equal(("6", (string?)null), ((string?)operators!["value"], (string?)operators["description"]));
        Assert.Equal(3, (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/settings", admin))).AsArray().Count);

        using var deleted = await SendAsync(HttpMethod.Delete, $"/admin/api/settings/chatbot/{Uri.EscapeDataString("Feature.Echo")}", admin);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var again = await SendAsync(HttpMethod.Delete, $"/admin/api/settings/chatbot/{Uri.EscapeDataString("Feature.Echo")}", admin);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        await using var ctx = Db.NewContext(s.TenantId);
        var audit = await ctx.AuditLogs.AsNoTracking().Where(a => a.Action.StartsWith("setting.")).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["setting.set", "setting.set", "setting.set", "setting.set", "setting.delete"], audit.Select(a => a.Action).ToArray());
        Assert.All(audit, a => Assert.Equal(s.TenantId, a.TenantId));
        var change = audit[1];
        Assert.Equal($"chatbot/{MaxOperators}", change.EntityId);
        Assert.Contains("\"Old\":\"4\"", change.DetailJson!);
        Assert.Contains("\"New\":\"6\"", change.DetailJson!);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_with_specific_codes_and_the_tenant_limit_is_enforced()
    {
        var s = await SetupAsync();
        var admin = await TenantTokenAsync(s.TenantId, Roles.Admin);

        async Task<string?> CodeAsync(string ns, string key, string value, string type, string? description = null)
        {
            using var response = await PutAsync(admin, ns, key, value, type, description);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return ErrorCode(await BodyAsync(response));
        }

        Assert.Equal("invalid_namespace", await CodeAsync("Chatbot", "k", "1", "int"));
        Assert.Equal("invalid_namespace", await CodeAsync("-chatbot", "k", "1", "int"));
        Assert.Equal("invalid_key", await CodeAsync("chatbot", ".k", "1", "int"));
        Assert.Equal("invalid_key", await CodeAsync("chatbot", "k k", "1", "int"));
        Assert.Equal("invalid_value_type", await CodeAsync("chatbot", "k", "1", "float"));
        Assert.Equal("invalid_value", await CodeAsync("chatbot", "k", "2147483648", "int"));
        Assert.Equal("invalid_value", await CodeAsync("chatbot", "k", "abc", "int"));
        Assert.Equal("invalid_value", await CodeAsync("chatbot", "k", "yes", "bool"));
        Assert.Equal("invalid_value", await CodeAsync("chatbot", "k", new string('x', 2001), "text"));
        Assert.Equal("invalid_description", await CodeAsync("chatbot", "k", "1", "int", new string('d', 201)));
        await using (var check = Db.NewContext(s.TenantId))
            Assert.Empty(await check.AppSettings.AsNoTracking().ToListAsync());

        await using (var ctx = Db.NewContext(s.TenantId))
        {
            ctx.AppSettings.AddRange(Enumerable.Range(0, 200).Select(i =>
                new AppSetting { Namespace = "bulk", Key = $"k{i}", Value = "1", ValueType = SettingValueTypes.Int }));
            await ctx.SaveChangesAsync();
        }

        using var overLimit = await PutAsync(admin, "bulk", "one-too-many", "1", SettingValueTypes.Int);
        Assert.Equal(HttpStatusCode.Conflict, overLimit.StatusCode);
        Assert.Equal("setting_limit", ErrorCode(await BodyAsync(overLimit)));
        // Mengubah yang sudah ada tetap boleh saat penuh.
        using var existing = await PutAsync(admin, "bulk", "k0", "9", SettingValueTypes.Int);
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_write_and_tenants_cannot_see_or_touch_each_others_settings()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var adminA = await TenantTokenAsync(a.TenantId, Roles.Admin);
        var viewerA = await TenantTokenAsync(a.TenantId, Roles.Viewer);
        var adminB = await TenantTokenAsync(b.TenantId, Roles.Owner);
        using (var seeded = await PutAsync(adminA, "chatbot", MaxOperators, "4", SettingValueTypes.Int))
            Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/settings", viewerA)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(viewerA, "chatbot", MaxOperators, "9", SettingValueTypes.Int)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Delete, $"/admin/api/settings/chatbot/{Uri.EscapeDataString(MaxOperators)}", viewerA)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/admin/api/settings", null)).StatusCode);

        var platformAdmin = await LoginAsync(SeedAdminEmail, SeedAdminPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/api/settings", platformAdmin)).StatusCode);

        // Tenant B tidak melihat, tidak bisa menghapus (404 sama dengan tidak ada), dan menulis baris miliknya sendiri.
        Assert.Empty((await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/settings", adminB))).AsArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Delete, $"/admin/api/settings/chatbot/{Uri.EscapeDataString(MaxOperators)}", adminB)).StatusCode);
        using (var own = await PutAsync(adminB, "chatbot", MaxOperators, "1", SettingValueTypes.Int))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var mine = Assert.Single((await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/settings", adminA))).AsArray());
        Assert.Equal("4", (string?)mine!["value"]);
    }

    [Fact]
    public async Task Client_reads_only_its_tenants_namespace_with_the_api_key_and_polls_with_etag()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var adminA = await TenantTokenAsync(a.TenantId, Roles.Admin);
        foreach (var (key, value, type) in new[] { (MaxOperators, "4", "int"), ("Dispatcher:SweepSeconds", "5", "int") })
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(adminA, "chatbot", key, value, type)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(adminA, "other", "Hidden", "x", "text")).StatusCode);

        using var first = await ClientGetAsync(a.ApiKey);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await BodyAsync(first);
        Assert.Equal(["Dispatcher:MaxConcurrentOperators", "Dispatcher:SweepSeconds"], body["items"]!.AsArray().Select(i => (string)i!["key"]!).ToArray());
        Assert.Equal(("4", "int"), ((string?)body["items"]![0]!["value"], (string?)body["items"]![0]!["valueType"]));
        var etag = first.Headers.ETag!.Tag;
        Assert.Equal($"\"{(string?)body["version"]}\"", etag);

        // Tidak berubah → 304 tanpa body; berubah atau dihapus → 200 dengan versi baru.
        using var unchanged = await ClientGetAsync(a.ApiKey, ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(adminA, "chatbot", MaxOperators, "8", "int")).StatusCode);
        using var changed = await ClientGetAsync(a.ApiKey, ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var changedTag = changed.Headers.ETag!.Tag;
        Assert.NotEqual(etag, changedTag);
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/admin/api/settings/chatbot/{Uri.EscapeDataString("Dispatcher:SweepSeconds")}", adminA)).StatusCode);
        using var afterDelete = await ClientGetAsync(a.ApiKey, ifNoneMatch: changedTag);
        Assert.Equal(HttpStatusCode.OK, afterDelete.StatusCode);
        Assert.Single((await BodyAsync(afterDelete))["items"]!.AsArray());

        // Key tenant lain hanya melihat miliknya sendiri (kosong).
        Assert.Empty((await BodyAsync(await ClientGetAsync(b.ApiKey)))["items"]!.AsArray());
    }

    [Fact]
    public async Task Client_rejections_use_the_gateway_error_format_and_leave_no_ai_usage_trace()
    {
        var s = await SetupAsync();

        async Task<(HttpStatusCode Status, string? Code)> CallAsync(string? key, string query = "?namespace=chatbot")
        {
            using var response = await ClientGetAsync(key, query);
            return (response.StatusCode, ErrorCode(await BodyAsync(response)));
        }

        Assert.Equal((HttpStatusCode.Unauthorized, "missing_api_key"), await CallAsync(null));
        Assert.Equal((HttpStatusCode.Unauthorized, "invalid_api_key"), await CallAsync("gw_zzzzzzzzzz_not-a-real-key"));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_namespace"), await CallAsync(s.ApiKey, ""));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_namespace"), await CallAsync(s.ApiKey, "?namespace=Bad%20Name"));

        await using (var ctx = Db.NewContext(s.TenantId))
        {
            await ctx.ApiKeys.Where(k => k.Id == s.KeyId).ExecuteUpdateAsync(k => k.SetProperty(x => x.RevokedAt, DateTime.UtcNow));
            Assert.Equal((HttpStatusCode.Unauthorized, "api_key_revoked"), await CallAsync(s.ApiKey));
            await ctx.ApiKeys.Where(k => k.Id == s.KeyId).ExecuteUpdateAsync(k => k.SetProperty(x => x.RevokedAt, (DateTime?)null));
        }

        await using (var ctx = Db.NewContext())
            await ctx.Tenants.Where(t => t.Id == s.TenantId).ExecuteUpdateAsync(t => t.SetProperty(x => x.Status, TenantStatuses.Suspended));
        Assert.Equal((HttpStatusCode.Forbidden, "tenant_suspended"), await CallAsync(s.ApiKey));

        // Data plane mencatat penolakan sebagai usage; pembacaan pengaturan tidak boleh meninggalkan jejak itu.
        await using var check = Db.NewContext(s.TenantId);
        Assert.Equal(0, await check.UsageLogs.CountAsync());
    }
}
