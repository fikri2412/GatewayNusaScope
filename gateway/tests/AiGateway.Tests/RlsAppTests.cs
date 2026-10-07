using System.Net;
using System.Net.Http.Json;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

/// <summary>
/// Jalur produksi dengan lapis kedua aktif: <c>gateway/sql/tenant-security.sql</c> dipasang di database uji
/// sebelum host start (lihat <see cref="GatewayFactory"/>), lalu data plane, login admin, dan aksi platform
/// dijalankan lewat host sungguhan.
///
/// Principal koneksi test adalah Windows user (= dbo di LocalDB) dan bukan anggota <c>gateway_platform</c>,
/// jadi predikat RLS berlaku apa adanya - termasuk jalur <c>api_key_bootstrap</c> di ApiKeyAuthenticator.
/// Test pertama kelas ini memverifikasi premis itu secara empiris lewat koneksi aplikasi.
/// </summary>
public class RlsAppTests(TestDb db) : GatewayTestBase(db, rls: true)
{
    private const string SeedAdminEmail = "test-admin@example.test";
    private const string SeedAdminPassword = "test-password-123456";

    [Fact]
    public async Task Rls_applies_to_the_app_connection()
    {
        var a = await SetupAsync(alias: "gpt-a");
        var b = await SetupAsync(alias: "gpt-b");

        await using var scope = Factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        // IgnoreQueryFilters melewatkan filter EF; kalau baris tenant lain masih terlihat di sini, RLS tidak
        // berlaku untuk principal koneksi test dan sisa kelas ini tidak bermakna.
        ctx.CurrentTenantId = GatewayDbContext.NoTenant;
        var withoutContext = await ctx.Providers.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.True(withoutContext.Count == 0,
            $"RLS tidak berlaku untuk koneksi test: {withoutContext.Count} provider terlihat tanpa tenant context.");
        Assert.NotEqual(a.TenantId, b.TenantId);

        ctx.CurrentTenantId = b.TenantId;
        var visible = await ctx.Providers.IgnoreQueryFilters().AsNoTracking().Select(p => p.TenantId).Distinct().ToListAsync();
        Assert.Equal(new[] { b.TenantId }, visible);
    }

    [Fact]
    public async Task Data_plane_chat_completion_works_through_the_bootstrap_procedure()
    {
        var s = await SetupAsync();

        // Prosedur wajib ada; kalau tidak, ApiKeyAuthenticator jatuh ke fallback 2812 (query langsung ke api_keys).
        Assert.NotNull(await ScalarAsync("SELECT OBJECT_ID(N'dbo.api_key_bootstrap', N'P');"));

        // Tanpa context tenant, jalur fallback (IgnoreQueryFilters ke api_keys) tidak menemukan baris apa pun,
        // jadi 200 di bawah ini hanya mungkin kalau prosedur bootstrap benar-benar dieksekusi.
        string prefix;
        await using (var owner = Db.NewContext(s.TenantId))
            prefix = (await owner.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == s.KeyId)).KeyPrefix;
        await using (var none = Db.NewContext())
            Assert.Empty(await none.ApiKeys.IgnoreQueryFilters().AsNoTracking()
                .Where(k => k.KeyPrefix == prefix).ToListAsync());

        using var response = await PostChatAsync(s.ApiKey, Chat());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Usage tercatat lewat koneksi aplikasi dengan RLS aktif (termasuk MERGE usage_daily).
        await using var usage = Db.NewContext(s.TenantId);
        var log = Assert.Single(await usage.UsageLogs.AsNoTracking()
            .Where(l => l.ProjectId == s.ProjectId && l.Status == UsageStatuses.Ok).ToListAsync());
        Assert.Equal(s.TenantId, log.TenantId);
    }

    [Fact]
    public async Task Login_audit_row_is_written_with_its_tenant_under_rls()
    {
        var (tenantId, userId, email) = await NewUserAsync();

        using var login = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Premis RLS (sama seperti test pertama, dari sudut pandang tabel audit): tanpa context tenant,
        // baris audit tenant mana pun tidak terlihat oleh koneksi aplikasi.
        await using (var none = Db.NewContext())
        {
            var hidden = await none.AuditLogs.AsNoTracking().Where(a => a.Action == "auth.login").ToListAsync();
            Assert.True(hidden.Count == 0, $"RLS tidak berlaku untuk koneksi test: {hidden.Count} baris audit terlihat tanpa tenant context.");
        }

        // Sebelum perbaikan, INSERT ditolak block predicate (tenant_id baris != tenant sesi, karena request
        // login anonim belum punya context tenant) dan error ditelan AuditWriter: baris hanya ada kalau
        // AuditWriter memasang context tenant untuk penulisan ini.
        await using var check = Db.NewContext(tenantId);
        var row = Assert.Single(await check.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "auth.login" && a.UserId == userId).ToListAsync());
        Assert.Equal(tenantId, row.TenantId);
    }

    [Fact]
    public async Task Failed_login_and_platform_action_do_not_fail_when_their_audit_rows_are_blocked()
    {
        // Login gagal dan aksi tingkat platform menulis baris audit tanpa tenant; baris itu ditolak block
        // predicate saat koneksi memakai principal gateway_app (limit yang didokumentasikan di script dan
        // AuditWriter). Yang diuji di sini: kegagalan audit tidak boleh menggagalkan aksi HTTP-nya.
        using (var wrong = await Client.PostAsJsonAsync("/admin/api/auth/login",
                   new { email = $"nobody-{Guid.NewGuid():N}@example.test", password = "wrong-password-123" }))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var login = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email = SeedAdminEmail, password = SeedAdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (string?)(await BodyAsync(login))["accessToken"];
        Assert.False(string.IsNullOrEmpty(token));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/platform/api/plans")
        {
            Content = JsonContent.Create(new { name = "plan-" + Guid.NewGuid().ToString("N")[..14] }),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var created = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(Db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
