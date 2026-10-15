using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

/// <summary>
/// Plane platform HTTP dengan RLS aktif dan <c>ConnectionStrings:GatewayPlatform</c> terisi: laporan lintas tenant
/// dan baris audit tanpa tenant lewat koneksi principal <c>gateway_platform</c>, sementara jalur tenant tetap di
/// koneksi aplikasi. Principal koneksi test (dbo) bukan anggota role itu, jadi <see cref="ImpersonatingPlatformDb"/>
/// menyamar sebagai user database yang menjadi anggotanya (EXECUTE AS, tanpa pooling), seperti
/// <see cref="SqlIsolationTests"/>. Tanpa koneksi platform perilaku lama diuji di <see cref="RlsAppTests"/>.
/// </summary>
public class PlatformRlsTests(TestDb db) : GatewayTestBase(db, rls: true,
    configureServices: services => services.AddSingleton<PlatformDb>(new ImpersonatingPlatformDb(db.ConnectionString, PlatformUser)))
{
    private const string PlatformUser = "gateway_platform_http_test";
    private const string SeedAdminEmail = "test-admin@example.test";
    private const string SeedAdminPassword = "test-password-123456";

    [Fact]
    public async Task Platform_reports_cover_every_tenant_while_tenant_reports_stay_isolated()
    {
        await EnsurePlatformUserAsync();
        var a = await SetupAsync(alias: "gpt-a");
        var b = await SetupAsync(alias: "gpt-b");
        foreach (var (scenario, alias) in new[] { (a, "gpt-a"), (b, "gpt-b") })
        {
            using var chat = await PostChatAsync(scenario.ApiKey, Chat(alias));
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        }

        var (_, ownerId, ownerEmail) = await NewUserAsync(Roles.Owner, a.TenantId);
        var owner = await LoginAsync(ownerEmail, StrongPassword);
        var admin = await LoginAsync(SeedAdminEmail, SeedAdminPassword);

        // Premis: koneksi aplikasi tanpa tenant context tidak melihat usage tenant mana pun.
        await using (var none = Db.NewContext())
            Assert.Empty(await none.UsageDailies.IgnoreQueryFilters().AsNoTracking().ToListAsync());

        var today = DateTime.UtcNow.Date;
        var range = $"from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";
        var usage = (await BodyAsync(await SendAsync(HttpMethod.Get, $"/platform/api/usage?{range}", admin))).AsArray();
        foreach (var tenantId in new[] { a.TenantId, b.TenantId })
        {
            var publicId = (await TenantPublicIdAsync(tenantId)).ToString();
            var row = Assert.Single(usage, r => (string?)r!["key"] == publicId);
            Assert.Equal(1, (int)row!["requests"]!);
        }

        // Jalur tenant tidak ikut menembus RLS: owner A hanya melihat satu request miliknya.
        var own = (await BodyAsync(await SendAsync(HttpMethod.Get, $"/admin/api/usage/summary?{range}&groupBy=day", owner))).AsArray();
        Assert.Equal(1, own.Sum(r => (int)r!["requests"]!));

        // Baris audit milik tenant (login owner) ikut terbaca laporan platform.
        var audit = (await BodyAsync(await SendAsync(HttpMethod.Get, "/platform/api/audit?pageSize=200", admin)))["items"]!.AsArray();
        Assert.Contains(audit, i => (string?)i!["action"] == "auth.login" && (long?)i["userId"] == ownerId);
    }

    [Fact]
    public async Task Tenantless_audit_rows_reach_audit_logs_and_the_platform_report()
    {
        await EnsurePlatformUserAsync();
        var ghost = $"ghost-{Guid.NewGuid():N}@example.test";
        using (var wrong = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email = ghost, password = "wrong-password-123" }))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var admin = await LoginAsync(SeedAdminEmail, SeedAdminPassword);
        using var created = await SendAsync(HttpMethod.Post, "/platform/api/plans", admin, new { name = "plan-" + Guid.NewGuid().ToString("N")[..14] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var planId = ((long)(await BodyAsync(created))["id"]!).ToString();

        // Premis: baris tanpa tenant hanya terlihat principal platform; koneksi aplikasi tidak melihatnya.
        await using (var none = Db.NewContext())
            Assert.Empty(await none.AuditLogs.AsNoTracking().Where(l => l.Action == "plan.create").ToListAsync());

        Assert.Equal(1, await PlatformCountAsync(
            "SELECT COUNT(*) FROM dbo.audit_logs WHERE tenant_id IS NULL AND action = N'auth.login_failed' AND detail_json LIKE @v;", $"%{ghost}%"));
        Assert.Equal(1, await PlatformCountAsync(
            "SELECT COUNT(*) FROM dbo.audit_logs WHERE tenant_id IS NULL AND action = N'plan.create' AND entity_id = @v;", planId));

        var audit = (await BodyAsync(await SendAsync(HttpMethod.Get, "/platform/api/audit?pageSize=200", admin)))["items"]!.AsArray();
        Assert.Contains(audit, i => (string?)i!["action"] == "plan.create" && (string?)i["entityId"] == planId);
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await BodyAsync(response))["accessToken"]!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    private async Task<Guid> TenantPublicIdAsync(long internalId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.Tenants.Where(t => t.Id == internalId).Select(t => t.PublicId).SingleAsync();
    }

    /// <summary>User database tanpa login yang menjadi anggota <c>gateway_platform</c> (role dibuat script RLS saat host start).</summary>
    private async Task EnsurePlatformUserAsync()
    {
        await using var conn = new SqlConnection(Db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF DATABASE_PRINCIPAL_ID(N'{PlatformUser}') IS NULL EXEC(N'CREATE USER {PlatformUser} WITHOUT LOGIN;');
            IF NOT EXISTS (SELECT 1 FROM sys.database_role_members AS m
                           JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
                           JOIN sys.database_principals AS u ON u.principal_id = m.member_principal_id
                           WHERE r.name = N'gateway_platform' AND u.name = N'{PlatformUser}')
                ALTER ROLE gateway_platform ADD MEMBER {PlatformUser};
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> PlatformCountAsync(string sql, string value)
    {
        // Tanpa pooling: EXECUTE AS tidak di-revert saat koneksi kembali ke pool.
        await using var conn = new SqlConnection(Db.ConnectionString + ";Pooling=false");
        await conn.OpenAsync();
        await using (var impersonate = conn.CreateCommand())
        {
            impersonate.CommandText = $"EXECUTE AS USER = N'{PlatformUser}';";
            await impersonate.ExecuteNonQueryAsync();
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@v", value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private sealed class ImpersonatingPlatformDb(string connectionString, string user)
        : PlatformDb(connectionString + ";Pooling=false")
    {
        protected override void Configure(DbContextOptionsBuilder<GatewayDbContext> options) =>
            options.AddInterceptors(new ExecuteAsOnOpen(user));
    }

    private sealed class ExecuteAsOnOpen(string user) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"EXECUTE AS USER = N'{user}';";
            cmd.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"EXECUTE AS USER = N'{user}';";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
