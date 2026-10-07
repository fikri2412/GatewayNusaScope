using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using AiGateway.Core.Reports;
using AiGateway.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Manajemen resource lewat HTTP sungguhan: isolasi lintas tenant untuk setiap endpoint (target asing 404,
/// daftar hanya milik sendiri), penolakan peran, dan pencegahan kebocoran secret/id internal.
/// </summary>
public class AdminResourceEndpointTests(TestDb db) : GatewayTestBase(db)
{
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

    /// <summary>Tenant penuh (provider/model/project/key) plus user owner yang bisa login HTTP.</summary>
    private async Task<(Scenario S, string Token)> OwnerTenantAsync()
    {
        var s = await SetupAsync();
        var (_, _, email) = await NewUserAsync(Roles.Owner, s.TenantId);
        return (s, await LoginAsync(email));
    }

    private async Task<Guid> TenantPublicIdAsync(long internalId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Tenants.Where(t => t.Id == internalId).Select(t => t.PublicId).SingleAsync();
    }

    private async Task<Guid> ProjectPublicIdAsync(long internalId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Projects.IgnoreQueryFilters().Where(p => p.Id == internalId).Select(p => p.PublicId).SingleAsync();
    }

    private async Task<Guid> KeyPublicIdAsync(long internalId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == internalId).Select(k => k.PublicId).SingleAsync();
    }

    private async Task<Guid> ModelPublicIdAsync(long internalId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Models.IgnoreQueryFilters().Where(m => m.Id == internalId).Select(m => m.PublicId).SingleAsync();
    }

    private async Task<Guid> ProviderPublicIdAsync(long internalId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Providers.IgnoreQueryFilters().Where(p => p.Id == internalId).Select(p => p.PublicId).SingleAsync();
    }

    private static string Iso(DateTime value) => Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Projects_crud_isolates_tenants_and_remaps_status()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();

        var created = await SendAsync(HttpMethod.Post, "/admin/api/projects", aToken, new { name = "proj-a", logContent = true, contentRetentionDays = 7 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await BodyAsync(created);
        var projectId = Guid.Parse((string)project["id"]!);
        Assert.Equal("proj-a", (string?)project["name"]);
        Assert.Equal("active", (string?)project["status"]);
        Assert.Equal(true, (bool?)project["logContent"]);
        Assert.Equal(7, (int?)project["contentRetentionDays"]);

        // daftar hanya memuat project milik tenant sendiri
        var listA = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/projects", aToken))).AsArray();
        Assert.Contains(listA, p => (string?)p!["id"] == projectId.ToString());
        var listB = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/projects", bToken))).AsArray();
        Assert.DoesNotContain(listB, p => (string?)p!["id"] == projectId.ToString());
        var bProjectId = (await ProjectPublicIdAsync(b.ProjectId)).ToString();
        Assert.DoesNotContain(listA, p => (string?)p!["id"] == bProjectId);

        // detail: milik sendiri OK, tenant lain 404
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/admin/api/projects/{projectId}", aToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/projects/{projectId}", bToken)).StatusCode);

        // ubah/hapus lintas tenant 404 dan tidak mengubah apa pun
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/admin/api/projects/{projectId}", bToken, new { name = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/admin/api/projects/{projectId}", bToken)).StatusCode);
        Assert.Equal("proj-a", (string?)(await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/projects/{projectId}", aToken)))["name"]);

        // PATCH: suspend + clearRetention (menang atas contentRetentionDays)
        var patched = await BodyAsync(await SendAsync(HttpMethod.Patch, $"/admin/api/projects/{projectId}", aToken,
            new { status = "suspended", logContent = false, contentRetentionDays = 30, clearRetention = true }));
        Assert.Equal("suspended", (string?)patched["status"]);
        Assert.Equal(false, (bool?)patched["logContent"]);
        Assert.Null(patched["contentRetentionDays"]);

        // DELETE → 204 lalu 404
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/api/projects/{projectId}", aToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/projects/{projectId}", aToken)).StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_write_and_missing_token_is_401()
    {
        var (s, _) = await OwnerTenantAsync();
        var (_, _, viewerEmail) = await NewUserAsync(Roles.Viewer, s.TenantId);
        var viewer = await LoginAsync(viewerEmail);
        var project = await ProjectPublicIdAsync(s.ProjectId);
        var key = await KeyPublicIdAsync(s.KeyId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/admin/api/projects", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/projects", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/keys", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/policies", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/summary?from={Day(DateTime.UtcNow.AddDays(-7))}&to={Day(DateTime.UtcNow)}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={Iso(DateTime.UtcNow.AddDays(-1))}&to={Iso(DateTime.UtcNow.AddDays(1))}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/audit", viewer)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/admin/api/projects", viewer, new { name = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Patch, $"/admin/api/projects/{project}", viewer, new { name = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/admin/api/projects/{project}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/admin/api/keys", viewer, new { projectId = project, name = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Patch, $"/admin/api/keys/{key}", viewer, new { name = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/admin/api/keys/{key}/revoke", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/admin/api/policies/project/{project}", viewer, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/admin/api/policies/project/{project}", viewer)).StatusCode);
    }

    [Fact]
    public async Task Admin_can_write_configuration_but_not_manage_users()
    {
        var (s, _) = await OwnerTenantAsync();
        var (_, _, adminEmail) = await NewUserAsync(Roles.Admin, s.TenantId);
        var admin = await LoginAsync(adminEmail);
        var project = await ProjectPublicIdAsync(s.ProjectId);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/api/projects", admin, new { name = "admin-made" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/api/policies/project/{project}", admin, new { enabled = false })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/api/users", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/admin/api/users/invite", admin, new { email = "a@example.test", displayName = "A", role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/admin/api/users/1/reset-password", admin)).StatusCode);
    }

    [Fact]
    public async Task Api_keys_issue_plaintext_once_filter_by_project_and_stay_isolated()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        var project = await ProjectPublicIdAsync(a.ProjectId);
        var bProject = await ProjectPublicIdAsync(b.ProjectId);

        var created = await SendAsync(HttpMethod.Post, "/admin/api/keys", aToken,
            new { projectId = project, name = "ci", allowedIps = new[] { "203.0.113.0/24" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await BodyAsync(created);
        var keyId = (string)body["id"]!;
        var plaintext = (string)body["plaintextKey"]!;
        Assert.StartsWith("gw_", plaintext);
        Assert.Equal(project, Guid.Parse((string)body["projectId"]!));
        Assert.Equal(new[] { "203.0.113.0/24" }, body["allowedIps"]!.AsArray().Select(x => (string)x!).ToArray());

        // plaintext dan hash tidak pernah muncul lagi (detail, daftar, maupun filter per project)
        foreach (var path in new[] { $"/admin/api/keys/{keyId}", "/admin/api/keys", $"/admin/api/keys?projectId={project}" })
        {
            var json = await (await SendAsync(HttpMethod.Get, path, aToken)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("plaintextKey", json);
            Assert.DoesNotContain("keyHash", json);
            Assert.DoesNotContain(plaintext, json);
        }

        // filter per project + daftar hanya milik tenant sendiri (project juga memuat key bawaan skenario)
        var filtered = (await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/keys?projectId={project}", aToken))).AsArray();
        Assert.Contains(filtered, k => (string?)k!["id"] == keyId);
        Assert.All(filtered, k => Assert.Equal(project, Guid.Parse((string)k!["projectId"]!)));
        var listB = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/keys", bToken))).AsArray();
        Assert.DoesNotContain(listB, k => (string?)k!["id"] == keyId);

        // detail/ubah/cabut lintas tenant 404; filter project tenant lain juga 404
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/keys/{keyId}", bToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/admin/api/keys/{keyId}", bToken, new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/admin/api/keys/{keyId}/revoke", bToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/admin/api/keys", bToken, new { projectId = project, name = "steal" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/keys?projectId={bProject}", aToken)).StatusCode);

        // PATCH sendiri: nama, IP, kedaluwarsa
        var patched = await BodyAsync(await SendAsync(HttpMethod.Patch, $"/admin/api/keys/{keyId}", aToken,
            new { name = "renamed", expiresAt = DateTime.UtcNow.AddDays(30), allowedIps = Array.Empty<string>() }));
        Assert.Equal("renamed", (string?)patched["name"]);
        Assert.Empty(patched["allowedIps"]!.AsArray());
        Assert.NotNull(patched["expiresAt"]);
        var cleared = await BodyAsync(await SendAsync(HttpMethod.Patch, $"/admin/api/keys/{keyId}", aToken, new { clearExpiry = true }));
        Assert.Null(cleared["expiresAt"]);

        // cabut sendiri → 204 dan revokedAt terisi
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/admin/api/keys/{keyId}/revoke", aToken)).StatusCode);
        Assert.NotNull((await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/keys/{keyId}", aToken)))["revokedAt"]);

        // audit mencatat aksi key tanpa plaintext
        var auditJson = await (await SendAsync(HttpMethod.Get, "/admin/api/audit", aToken)).Content.ReadAsStringAsync();
        Assert.Contains("key.create", auditJson);
        Assert.DoesNotContain(plaintext, auditJson);
    }

    [Fact]
    public async Task Policies_are_upserted_listed_and_deleted_per_scope()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        var tenantPublic = await TenantPublicIdAsync(a.TenantId);
        var project = await ProjectPublicIdAsync(a.ProjectId);
        var key = await KeyPublicIdAsync(a.KeyId);

        var put = await BodyAsync(await SendAsync(HttpMethod.Put, $"/admin/api/policies/tenant/{tenantPublic}", aToken,
            new { maxTokensPerRequest = 1000, requestsPerMinute = 10, allowedModels = new[] { "gpt-test" }, enabled = true }));
        Assert.Equal("tenant", (string?)put["scope"]);
        Assert.Equal(tenantPublic, Guid.Parse((string)put["scopeId"]!));
        Assert.Equal(1000, (int?)put["maxTokensPerRequest"]);
        Assert.Equal(new[] { "gpt-test" }, put["allowedModels"]!.AsArray().Select(x => (string)x!).ToArray());

        await SendAsync(HttpMethod.Put, $"/admin/api/policies/project/{project}", aToken, new { dailyTokenQuota = 5000, enabled = true });
        var putKey = await BodyAsync(await SendAsync(HttpMethod.Put, $"/admin/api/policies/key/{key}", aToken, new { monthlyBudget = 12.5m, enabled = false }));
        Assert.Equal(12.5m, (decimal?)putKey["monthlyBudget"]);
        Assert.Equal(false, (bool?)putKey["enabled"]);

        // detail per scope
        var got = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/policies/project/{project}", aToken));
        Assert.Equal(5000, (long?)got["dailyTokenQuota"]);

        // daftar memakai GUID publik, hanya milik tenant sendiri
        var list = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/policies", aToken))).AsArray();
        Assert.Equal(3, list.Count);
        Assert.Contains(list, p => (string?)p!["scope"] == "tenant" && (string?)p!["scopeId"] == tenantPublic.ToString());
        Assert.Contains(list, p => (string?)p!["scope"] == "key" && (string?)p!["scopeId"] == key.ToString());
        Assert.Empty((await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/policies", bToken))).AsArray());

        // target lintas tenant → 404 untuk GET/PUT/DELETE, termasuk scope tenant dan key
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/policies/project/{project}", bToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/admin/api/policies/project/{project}", bToken, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/admin/api/policies/project/{project}", bToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/policies/tenant/{tenantPublic}", bToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/admin/api/policies/tenant/{tenantPublic}", bToken, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/policies/key/{key}", bToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/admin/api/policies/key/{key}", bToken)).StatusCode);

        // scope tidak valid 400, target tidak ada 404, DELETE lalu GET 404
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, $"/admin/api/policies/bogus/{project}", aToken, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/policies/project/{Guid.NewGuid()}", aToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/api/policies/project/{project}", aToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/policies/project/{project}", aToken)).StatusCode);
    }

    [Fact]
    public async Task Usage_summary_and_logs_are_scoped_and_expose_public_ids()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        await PostChatAsync(a.ApiKey, Chat("missing-model")); // ditolak, tanpa model
        await PostChatAsync(a.ApiKey, Chat());
        await PostChatAsync(b.ApiKey, Chat());

        var project = await ProjectPublicIdAsync(a.ProjectId);
        var key = await KeyPublicIdAsync(a.KeyId);
        var model = await ModelPublicIdAsync(a.ModelId);
        var day = Day(DateTime.UtcNow);
        var from = Iso(DateTime.UtcNow.AddDays(-1));
        var to = Iso(DateTime.UtcNow.AddDays(1));

        var summary = (await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/summary?from={day}&to={day}&groupBy=project", aToken))).AsArray();
        var row = Assert.Single(summary);
        Assert.Equal(project.ToString(), (string?)row!["key"]);
        Assert.Equal(("p", (long?)2, (long?)1), ((string?)row["label"], (long?)row["requests"], (long?)row["denied"]));

        var byModel = (await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/summary?from={day}&to={day}&groupBy=model", aToken))).AsArray();
        Assert.Contains(byModel, r => (string?)r!["key"] == model.ToString());
        var byKey = (await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/summary?from={day}&to={day}&groupBy=key", aToken))).AsArray();
        Assert.Equal(key.ToString(), (string?)Assert.Single(byKey)!["key"]);

        // tenant B hanya melihat pemakaiannya sendiri
        var bSummary = (await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/summary?from={day}&to={day}&groupBy=project", bToken))).AsArray();
        Assert.Equal((await ProjectPublicIdAsync(b.ProjectId)).ToString(), (string?)Assert.Single(bSummary)!["key"]);

        // log: id internal tidak pernah tampil, hanya GUID publik
        var logs = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}", aToken));
        Assert.Equal(2, (int?)logs["total"]);
        var newest = logs["items"]!.AsArray()[0]!;
        Assert.Equal(project.ToString(), (string?)newest["projectId"]);
        Assert.Equal(key.ToString(), (string?)newest["keyId"]);
        Assert.Equal(model.ToString(), (string?)newest["modelId"]);
        Assert.True(Guid.TryParse((string?)newest["requestId"], out _));
        Assert.DoesNotContain("tenantId", logs.ToJsonString());
        var denied = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}&status=denied", aToken));
        Assert.Equal(1, (int?)denied["total"]);
        Assert.Null(denied["items"]!.AsArray()[0]!["modelId"]);

        // filter dengan GUID resource tenant lain → 404
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}&projectId={await ProjectPublicIdAsync(b.ProjectId)}", aToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}&keyId={await KeyPublicIdAsync(b.KeyId)}", aToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}&modelId={await ModelPublicIdAsync(b.ModelId)}", aToken)).StatusCode);

        // tenant B tidak melihat log tenant A
        Assert.Equal(1, (int?)(await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}", bToken)))["total"]);
    }

    [Fact]
    public async Task Usage_export_returns_csv_with_public_ids_only()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        await PostChatAsync(a.ApiKey, Chat());
        await PostChatAsync(b.ApiKey, Chat());
        var from = Iso(DateTime.UtcNow.AddDays(-1));
        var to = Iso(DateTime.UtcNow.AddDays(1));

        var response = await SendAsync(HttpMethod.Get, $"/admin/api/usage/export?from={from}&to={to}", aToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // header + hanya baris tenant A
        Assert.StartsWith("id,created_at,request_id,project_id,api_key_id,model_id,", lines[0]);
        Assert.Contains((await ProjectPublicIdAsync(a.ProjectId)).ToString(), lines[1]);
        Assert.Contains((await KeyPublicIdAsync(a.KeyId)).ToString(), lines[1]);
        Assert.DoesNotContain((await ProjectPublicIdAsync(b.ProjectId)).ToString(), csv);

        // filter lintas tenant juga 404 di export
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/export?from={from}&to={to}&keyId={await KeyPublicIdAsync(b.KeyId)}", aToken)).StatusCode);
    }

    [Fact]
    public async Task User_management_is_owner_only_and_tenant_scoped()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        var (_, _, adminEmail) = await NewUserAsync(Roles.Admin, a.TenantId);
        var admin = await LoginAsync(adminEmail);
        var (_, _, viewerEmail) = await NewUserAsync(Roles.Viewer, a.TenantId);
        var viewer = await LoginAsync(viewerEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/api/users", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/admin/api/users/invite", admin, new { email = "x@example.test", displayName = "X", role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/api/users", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Patch, $"/admin/api/users/1", viewer, new { role = "owner" })).StatusCode);

        var inviteEmail = $"inv-{Guid.NewGuid():N}@example.test";
        var invited = await SendAsync(HttpMethod.Post, "/admin/api/users/invite", aToken, new { email = inviteEmail, displayName = "Invited", role = "viewer" });
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        var body = await BodyAsync(invited);
        var invitedId = (long)body["user"]!["id"]!;
        Assert.Equal("viewer", (string?)body["user"]!["role"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)body["inviteToken"]));

        var listJson = await (await SendAsync(HttpMethod.Get, "/admin/api/users", aToken)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", listJson);
        Assert.DoesNotContain((string)body["inviteToken"]!, listJson); // token undangan hanya tampil sekali
        Assert.Contains(inviteEmail, listJson);
        var otherList = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/users", bToken))).AsArray();
        Assert.DoesNotContain(otherList, u => (string?)u!["email"] == inviteEmail);

        // PATCH peran/status dan token reset
        var updated = await BodyAsync(await SendAsync(HttpMethod.Patch, $"/admin/api/users/{invitedId}", aToken, new { role = "admin" }));
        Assert.Equal("admin", (string?)updated["role"]);
        var reset = await BodyAsync(await SendAsync(HttpMethod.Post, $"/admin/api/users/{invitedId}/reset-password", aToken));
        var resetToken = (string)reset["token"]!;
        Assert.False(string.IsNullOrWhiteSpace(resetToken));

        // audit mencatat aksi user tanpa token
        var auditJson = await (await SendAsync(HttpMethod.Get, "/admin/api/audit", aToken)).Content.ReadAsStringAsync();
        Assert.Contains("user.invite", auditJson);
        Assert.Contains("user.reset_password", auditJson);
        Assert.DoesNotContain((string)body["inviteToken"]!, auditJson);
        Assert.DoesNotContain(resetToken, auditJson);

        // user tenant lain → 404 (filter tenant eksplisit), bukan efek samping
        var otherUser = await NewUserAsync(Roles.Owner, b.TenantId);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/admin/api/users/{otherUser.UserId}", aToken, new { role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/admin/api/users/{otherUser.UserId}/reset-password", aToken)).StatusCode);
    }

    [Fact]
    public async Task Audit_is_explicitly_tenant_filtered_and_paged()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (_, bToken) = await OwnerTenantAsync();

        var createdA = await BodyAsync(await SendAsync(HttpMethod.Post, "/admin/api/projects", aToken, new { name = "audited-a" }));
        var createdB = await BodyAsync(await SendAsync(HttpMethod.Post, "/admin/api/projects", bToken, new { name = "audited-b" }));
        var projectA = (string)createdA["id"]!;
        var projectB = (string)createdB["id"]!;

        var page = await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/audit?page=1&pageSize=50", aToken));
        var items = page["items"]!.AsArray();
        await using (var auditCtx = Db.NewContext())
            Assert.Equal(await auditCtx.AuditLogs.CountAsync(x => x.TenantId == a.TenantId), (int?)page["total"]); // persis baris tenant ini
        Assert.Equal(((int?)1, (int?)50), ((int?)page["pageNumber"], (int?)page["pageSize"]));
        var row = Assert.Single(items, i => (string?)i!["action"] == "project.create" && (string?)i!["entityId"] == projectA);
        Assert.Equal("project", (string?)row!["entity"]);
        Assert.NotNull(row["userId"]);
        Assert.DoesNotContain(items, i => (string?)i!["entityId"] == projectB);

        // rentang waktu dihormati (from eksklusif ke depan)
        var empty = await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/audit?from={Iso(DateTime.UtcNow.AddHours(1))}", aToken));
        Assert.Equal(0, (int?)empty["total"]);

        // viewer boleh membaca; tenant lain tidak melihat baris ini
        var (_, _, viewerEmail) = await NewUserAsync(Roles.Viewer, a.TenantId);
        var viewerPage = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/audit", await LoginAsync(viewerEmail))))["items"]!.AsArray();
        Assert.Contains(viewerPage, i => (string?)i!["entityId"] == projectA);
        var bItems = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/audit", bToken)))["items"]!.AsArray();
        Assert.DoesNotContain(bItems, i => (string?)i!["entityId"] == projectA);
    }

    [Fact]
    public async Task Invalid_inputs_are_rejected()
    {
        var (s, token) = await OwnerTenantAsync();
        var project = await ProjectPublicIdAsync(s.ProjectId);
        var from = Iso(DateTime.UtcNow.AddDays(-1));
        var to = Iso(DateTime.UtcNow.AddDays(1));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/admin/api/usage/summary?from=nope&to=nope", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/summary?from={Day(DateTime.UtcNow)}&to={Day(DateTime.UtcNow)}&groupBy=tenant", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from=nope&to={to}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}&status=bogus", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, $"/admin/api/policies/bogus/{project}", token, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Patch, $"/admin/api/projects/{project}", token, new { status = "archived" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/admin/api/keys", token, new { projectId = project, name = "x", allowedIps = new[] { "not-an-ip" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/admin/api/users/invite", token, new { email = "bad", displayName = "X", role = "viewer" })).StatusCode);
    }

    [Fact]
    public async Task Provider_and_model_routes_isolate_tenants_and_reject_viewers()
    {
        var (a, aToken) = await OwnerTenantAsync();
        var (b, bToken) = await OwnerTenantAsync();
        var (_, _, viewerEmail) = await NewUserAsync(Roles.Viewer, a.TenantId);
        var viewer = await LoginAsync(viewerEmail);
        var foreignProvider = await ProviderPublicIdAsync(b.ProviderId);
        var foreignModel = await ModelPublicIdAsync(b.ModelId);

        // Id milik tenant lain: 404 untuk baca maupun semua aksi (termasuk id di dalam body).
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"/admin/api/providers/{foreignProvider}", null),
                     (HttpMethod.Patch, $"/admin/api/providers/{foreignProvider}", new { name = "hijack" }),
                     (HttpMethod.Delete, $"/admin/api/providers/{foreignProvider}", null),
                     (HttpMethod.Post, $"/admin/api/providers/{foreignProvider}/rotate-key", new { apiKey = "sk-hijack-1234567890" }),
                     (HttpMethod.Post, $"/admin/api/providers/{foreignProvider}/discover", null),
                     (HttpMethod.Post, $"/admin/api/providers/{foreignProvider}/import-models", new { source = "discovered", upstreamModels = new[] { "m" } }),
                     (HttpMethod.Get, $"/admin/api/models/{foreignModel}", null),
                     (HttpMethod.Patch, $"/admin/api/models/{foreignModel}", new { enabled = false }),
                     (HttpMethod.Delete, $"/admin/api/models/{foreignModel}", null),
                     (HttpMethod.Put, $"/admin/api/models/{foreignModel}/routes", new { routes = Array.Empty<object>() }),
                     (HttpMethod.Post, $"/admin/api/models/{foreignModel}/prices", new { input = 1m, output = 2m }),
                 })
        {
            var response = await SendAsync(method, path, aToken, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} -> {(int)response.StatusCode}");
        }

        // Tanpa efek samping: provider/model tenant lain tetap utuh dan tidak muncul di daftar tenant A.
        var bProviders = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/providers", bToken))).AsArray();
        Assert.Contains(bProviders, p => (Guid?)p!["id"] == foreignProvider);
        var aProviders = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/providers", aToken))).AsArray();
        Assert.DoesNotContain(aProviders, p => (Guid?)p!["id"] == foreignProvider);
        var aModels = (await BodyAsync(await SendAsync(HttpMethod.Get, "/admin/api/models", aToken))).AsArray();
        Assert.DoesNotContain(aModels, m => (Guid?)m!["id"] == foreignModel);

        // Viewer: baca daftar boleh, seluruh aksi tulis ditolak.
        var ownProvider = await ProviderPublicIdAsync(a.ProviderId);
        var ownModel = await ModelPublicIdAsync(a.ModelId);
        foreach (var (method, path) in new (HttpMethod, string)[]
                 {
                     (HttpMethod.Post, "/admin/api/providers"),
                     (HttpMethod.Patch, $"/admin/api/providers/{ownProvider}"),
                     (HttpMethod.Delete, $"/admin/api/providers/{ownProvider}"),
                     (HttpMethod.Post, $"/admin/api/providers/{ownProvider}/rotate-key"),
                     (HttpMethod.Post, $"/admin/api/providers/{ownProvider}/discover"),
                     (HttpMethod.Post, $"/admin/api/providers/{ownProvider}/import-models"),
                     (HttpMethod.Post, "/admin/api/models"),
                     (HttpMethod.Patch, $"/admin/api/models/{ownModel}"),
                     (HttpMethod.Delete, $"/admin/api/models/{ownModel}"),
                     (HttpMethod.Put, $"/admin/api/models/{ownModel}/routes"),
                     (HttpMethod.Post, $"/admin/api/models/{ownModel}/prices"),
                 })
        {
            var response = await SendAsync(method, path, viewer, body: new { });
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/providers", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/admin/api/models", viewer)).StatusCode);
    }

    [Fact]
    public async Task Importing_more_models_than_the_cap_is_rejected()
    {
        var (s, token) = await OwnerTenantAsync();
        var provider = await ProviderPublicIdAsync(s.ProviderId);

        var response = await SendAsync(HttpMethod.Post, $"/admin/api/providers/{provider}/import-models", token,
            new { source = "discovered", upstreamModels = Enumerable.Range(0, 501).Select(i => $"model-{i}").ToArray() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("too_many_models", ErrorCode(await BodyAsync(response)));
        await using var ctx = Db.NewContext(s.TenantId);
        Assert.False(await ctx.Models.AnyAsync(m => m.Alias == "model-0"));
    }

    [Fact]
    public async Task Model_creation_rejects_invalid_price_tiers_and_currency_with_400()
    {
        var (s, token) = await OwnerTenantAsync();
        var provider = await ProviderPublicIdAsync(s.ProviderId);

        object NewModel(string alias, object price) => new
        {
            alias, routes = new[] { new { providerId = provider, upstreamModel = "real-model" } }, price,
        };

        var negativeTier = await SendAsync(HttpMethod.Post, "/admin/api/models", token, NewModel("neg-tier",
            new { input = 1m, output = 2m, tiers = new[] { new { minInputTokens = 1000, input = -5m, output = 1m } } }));
        Assert.Equal(HttpStatusCode.BadRequest, negativeTier.StatusCode);
        Assert.Equal("invalid_price", ErrorCode(await BodyAsync(negativeTier)));

        var duplicateTiers = await SendAsync(HttpMethod.Post, "/admin/api/models", token, NewModel("dup-tier",
            new { input = 1m, output = 2m, tiers = new[]
                {
                    new { minInputTokens = 1000, input = 2m, output = 3m },
                    new { minInputTokens = 1000, input = 4m, output = 5m },
                } }));
        Assert.Equal(HttpStatusCode.BadRequest, duplicateTiers.StatusCode);
        Assert.Equal("invalid_price", ErrorCode(await BodyAsync(duplicateTiers)));

        var longCurrency = await SendAsync(HttpMethod.Post, "/admin/api/models", token, NewModel("bad-currency",
            new { input = 1m, output = 2m, currency = "USDD" }));
        Assert.Equal(HttpStatusCode.BadRequest, longCurrency.StatusCode);
        Assert.Equal("invalid_currency", ErrorCode(await BodyAsync(longCurrency)));

        var hugePrice = await SendAsync(HttpMethod.Post, "/admin/api/models", token, NewModel("huge-price",
            new { input = 10_000_000_000_000m, output = 2m }));
        Assert.Equal(HttpStatusCode.BadRequest, hugePrice.StatusCode);
        Assert.Equal("invalid_price", ErrorCode(await BodyAsync(hugePrice)));

        // Tidak ada model, route, atau harga yang tertinggal dari empat permintaan itu.
        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Empty(await ctx.Models.Where(m => m.Alias.EndsWith("-tier") || m.Alias == "bad-currency" || m.Alias == "huge-price").ToListAsync());
        Assert.Empty(await ctx.ModelPrices.Where(p => p.InputPricePer1M < 0).ToListAsync());
    }

    [Fact]
    public async Task Rejected_optional_fields_do_not_leave_the_resource_behind()
    {
        var (s, token) = await OwnerTenantAsync();
        var projectName = $"partial-{Guid.NewGuid():N}"[..24];

        var project = await SendAsync(HttpMethod.Post, "/admin/api/projects", token, new { name = projectName, contentRetentionDays = 99_999 });
        Assert.Equal(HttpStatusCode.BadRequest, project.StatusCode);
        Assert.Equal("invalid_retention", ErrorCode(await BodyAsync(project)));

        var modelAlias = $"partial-{Guid.NewGuid():N}"[..24];
        var provider = await ProviderPublicIdAsync(s.ProviderId);
        var model = await SendAsync(HttpMethod.Post, "/admin/api/models", token, new
        {
            alias = modelAlias, description = new string('x', 501),
            routes = new[] { new { providerId = provider, upstreamModel = "real-model" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, model.StatusCode);
        Assert.Equal("invalid_description", ErrorCode(await BodyAsync(model)));

        await using var ctx = Db.NewContext(s.TenantId);
        Assert.False(await ctx.Projects.AnyAsync(p => p.Name == projectName));
        Assert.False(await ctx.Models.AnyAsync(m => m.Alias == modelAlias));
    }

    [Fact]
    public async Task Huge_page_numbers_return_an_empty_page_instead_of_an_error()
    {
        var (s, token) = await OwnerTenantAsync();
        await PostChatAsync(s.ApiKey, Chat());
        var from = Iso(DateTime.UtcNow.AddDays(-1));
        var to = Iso(DateTime.UtcNow.AddDays(1));

        var response = await SendAsync(HttpMethod.Get, $"/admin/api/usage/logs?from={from}&to={to}&page=10737420&pageSize=200", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(1, (int?)body["total"]);
        Assert.Empty(body["items"]!.AsArray());
        Assert.Equal(UsageReports.MaxPageNumber, (int?)body["pageNumber"]); // diklem, bukan overflow ke offset negatif

        var audit = await SendAsync(HttpMethod.Get, "/admin/api/audit?page=10737420&pageSize=200", token);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Empty((await BodyAsync(audit))["items"]!.AsArray());
    }

    [Fact]
    public async Task Usage_export_neutralises_formulas_and_escapes_quotes()
    {
        var (a, aToken) = await OwnerTenantAsync();
        await PostChatAsync(a.ApiKey, Chat(extra: ",\"user\":\"=HYPERLINK(\\\"http://evil\\\")\""), r => r.Headers.Add("X-Gateway-Tags", "a,\"b\""));
        var from = Iso(DateTime.UtcNow.AddDays(-1));
        var to = Iso(DateTime.UtcNow.AddDays(1));

        var response = await SendAsync(HttpMethod.Get, $"/admin/api/usage/export?from={from}&to={to}", aToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length); // header + satu baris
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", lines[1]); // rumus dinetralkan lalu di-quote
        Assert.Contains("\"a,\"\"b\"\"\"", lines[1]);
    }

    [Fact]
    public async Task Provider_keys_never_reach_audit_rows_or_responses()
    {
        var (s, token) = await OwnerTenantAsync();

        var created = await SendAsync(HttpMethod.Post, "/admin/api/providers", token,
            new { name = "audited", baseUrl = "https://audited.test/v1", apiKey = UpstreamKey });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var providerId = (string)(await BodyAsync(created))["id"]!;
        Assert.DoesNotContain(UpstreamKey, await created.Content.ReadAsStringAsync());

        var rotated = await SendAsync(HttpMethod.Post, $"/admin/api/providers/{providerId}/rotate-key", token, new { apiKey = UpstreamKey + "-baru" });
        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        Assert.DoesNotContain(UpstreamKey, await rotated.Content.ReadAsStringAsync());

        var listJson = await (await SendAsync(HttpMethod.Get, "/admin/api/providers", token)).Content.ReadAsStringAsync();
        Assert.Contains(providerId, listJson);
        Assert.Contains(ProviderKeyProtector.Hint(UpstreamKey), listJson); // hanya 4 karakter terakhir
        Assert.DoesNotContain(UpstreamKey, listJson);

        await using var ctx = Db.NewContext();
        var rows = await ctx.AuditLogs.AsNoTracking().Where(x => x.TenantId == s.TenantId).ToListAsync();
        Assert.Contains(rows, r => r.Action == "provider.create" && r.EntityId == providerId);
        Assert.Contains(rows, r => r.Action == "provider.rotate_key" && r.EntityId == providerId);
        Assert.All(rows, r => Assert.DoesNotContain(UpstreamKey, r.DetailJson ?? ""));
        var create = rows.Single(r => r.Action == "provider.create" && r.EntityId == providerId);
        Assert.NotNull(create.UserId);
    }
}
