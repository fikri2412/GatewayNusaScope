using System.Data.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Isolasi tenant lapis kedua di database sungguhan (LocalDB): RLS dari
/// <c>gateway/sql/tenant-security.sql</c>, predikat SESSION_CONTEXT, block predicate tulis,
/// bootstrap api key pre-tenant, audit log append-only, dan <see cref="TenantSessionInterceptor"/>
/// (koneksi pool + koneksi yang sudah terbuka). Penegakan diperiksa sebagai user database aplikasi
/// lewat EXECUTE AS USER — bukan lewat EF query filter.
/// </summary>
public class SqlIsolationTests(TestDb testDb) : IClassFixture<TestDb>
{
    private const string AppUser = "gw_app_test";
    private const string PlatformUser = "gw_platform_test";

    /// <summary>Tabel yang punya kolom <c>tenant_id</c> menurut skema (bukan daftar tangan).</summary>
    private static async Task<List<string>> TablesWithTenantIdAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name FROM sys.tables AS t
            JOIN sys.columns AS c ON c.object_id = t.object_id AND c.name = N'tenant_id'
            WHERE t.is_ms_shipped = 0;
            """;
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        // Pengecualian yang didokumentasikan di script: users adalah tabel global (platform admin tenant_id NULL).
        Assert.Contains("users", tables);
        tables.Remove("users");
        return tables;
    }

    private Task? _prepare;

    private Task PrepareAsync() => _prepare ??= PrepareCoreAsync();

    /// <summary>
    /// Script dijalankan dua kali (idempoten) lewat jalur yang sama dengan host aplikasi
    /// (<see cref="TestDb.InstallTenantSecurity"/>), lalu user uji tanpa login dibuat dan dimasukkan ke role.
    /// </summary>
    private async Task PrepareCoreAsync()
    {
        testDb.InstallTenantSecurity();
        testDb.InstallTenantSecurity();
        await using var conn = new SqlConnection(testDb.ConnectionString);
        await conn.OpenAsync();
        foreach (var (user, role) in new[] { (AppUser, "gateway_app"), (PlatformUser, "gateway_platform") })
        {
            await ExecAsync(conn, $"IF DATABASE_PRINCIPAL_ID(N'{user}') IS NULL EXEC(N'CREATE USER {user} WITHOUT LOGIN;');");
            await ExecAsync(conn, $"""
                IF NOT EXISTS (SELECT 1 FROM sys.database_role_members AS m
                               JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
                               JOIN sys.database_principals AS u ON u.principal_id = m.member_principal_id
                               WHERE r.name = N'{role}' AND u.name = N'{user}')
                    ALTER ROLE {role} ADD MEMBER {user};
                """);
        }
    }

    [Fact]
    public async Task Script_is_idempotent_and_installs_roles_policy_and_bootstrap_proc()
    {
        await PrepareAsync();
        await using var conn = new SqlConnection(testDb.ConnectionString);
        await conn.OpenAsync();

        Assert.Equal(3, Convert.ToInt32(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM sys.database_principals WHERE type = 'R' AND name IN (N'gateway_app', N'gateway_platform', N'gateway_migration');")));
        foreach (var policy in new[] { "tenant_isolation_policy", "maintenance_isolation_policy" })
            Assert.True(Convert.ToBoolean(await ScalarAsync(conn,
                $"SELECT is_enabled FROM sys.security_policies WHERE name = N'{policy}';")), policy);
        // Setiap tabel yang punya tenant_id (kecuali users) ditutup tepat FILTER + BLOCK INSERT + BLOCK UPDATE.
        // Daftar diambil dari skema supaya tabel tenant baru yang lupa ditutup langsung ketahuan; job_runs
        // (platform, tanpa tenant_id) tidak boleh masuk.
        var expected = await TablesWithTenantIdAsync(conn);
        var covered = await PredicateCountsAsync(conn);
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), covered.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(covered.Values, count => Assert.Equal(3, count));
        Assert.NotNull(await ScalarAsync(conn, "SELECT OBJECT_ID(N'dbo.api_key_bootstrap', N'P');"));
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(conn, """
            SELECT COUNT(*) FROM sys.database_permissions
            WHERE state_desc = 'DENY' AND permission_name IN ('UPDATE', 'DELETE')
              AND grantee_principal_id = DATABASE_PRINCIPAL_ID(N'gateway_app')
              AND major_id = OBJECT_ID(N'dbo.audit_logs');
            """)));
    }

    [Fact]
    public async Task App_user_reads_only_its_session_tenant_and_fails_closed_without_context()
    {
        await PrepareAsync();
        var (a, b) = await SeedTenantsAsync();

        await using var conn = await OpenAsAsync(AppUser);
        // Bersihkan eksplisit: jangan bergantung pada state yang mungkin ditinggalkan pool.
        await SetTenantAsync(conn, null);
        Assert.Equal(0, await ProviderCountAsync(conn)); // tanpa session context: fail closed

        await SetTenantAsync(conn, a.Id);
        Assert.Equal($"{a.Id}:1", await ProviderSummaryAsync(conn));

        await SetTenantAsync(conn, b.Id);
        Assert.Equal($"{b.Id}:1", await ProviderSummaryAsync(conn));
    }

    [Fact]
    public async Task App_cannot_read_or_write_other_tenant_even_with_session_platform_flag()
    {
        await PrepareAsync();
        var (a, b) = await SeedTenantsAsync();

        await using var conn = await OpenAsAsync(AppUser);
        await SetTenantAsync(conn, a.Id);
        // Flag yang bisa di-set klien; predikat hanya peduli keanggotaan role database.
        await ExecAsync(conn, "EXEC sys.sp_set_session_context @key = N'platform', @value = 1;");

        Assert.Equal($"{a.Id}:1", await ProviderSummaryAsync(conn));

        Assert.Equal(0, await ExecNonQueryAsync(conn,
            "UPDATE dbo.providers SET name = N'diretas' WHERE tenant_id = @t;", ("@t", b.Id)));
        Assert.Equal(0, await ExecNonQueryAsync(conn,
            "DELETE FROM dbo.providers WHERE tenant_id = @t;", ("@t", b.Id)));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(conn, """
            INSERT INTO dbo.providers (tenant_id, name, type, base_url, auth_header, auth_prefix, enabled)
            VALUES (@t, N'forged', N'openai', N'https://forged.test/v1/', N'Authorization', N'Bearer ', 1);
            """, ("@t", b.Id)));

        // Baris tenant B tetap utuh.
        await SetTenantAsync(conn, b.Id);
        Assert.Equal($"{b.Id}:1", await ProviderSummaryAsync(conn));
        Assert.Equal("pb", await ScalarAsync(conn, "SELECT name FROM dbo.providers;"));
    }

    [Fact]
    public async Task Platform_role_membership_bypasses_rls_and_app_cannot_impersonate_it()
    {
        await PrepareAsync();
        var (a, b) = await SeedTenantsAsync();

        await using (var platform = await OpenAsAsync(PlatformUser))
        {
            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(platform,
                "SELECT COUNT(*) FROM dbo.providers WHERE tenant_id IN (@a, @b);", ("@a", a.Id), ("@b", b.Id))));
            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(platform,
                "SELECT COUNT(DISTINCT tenant_id) FROM dbo.providers WHERE tenant_id IN (@a, @b);", ("@a", a.Id), ("@b", b.Id))));
        }

        await using var app = await OpenAsAsync(AppUser);
        await SetTenantAsync(app, a.Id);
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(app, "SELECT IS_MEMBER(N'gateway_platform');")));
        // Hanya baris tenant sendiri yang terlihat dari dua tenant uji.
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(app,
            "SELECT COUNT(*) FROM dbo.providers WHERE tenant_id IN (@a, @b);", ("@a", a.Id), ("@b", b.Id))));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(app, $"EXECUTE AS USER = N'{PlatformUser}';"));
    }

    [Fact]
    public async Task Api_key_bootstrap_maps_tenant_before_tenant_is_known()
    {
        await PrepareAsync();
        var a = await testDb.NewTenantAsync();
        var b = await testDb.NewTenantAsync();
        var projectId = await SeedProjectAsync(a.Id);
        var prefix = "gw_" + Guid.NewGuid().ToString("N")[..8];
        var hash = new string('f', 64);
        await using (var seed = testDb.NewContext(a.Id))
        {
            seed.ApiKeys.Add(new ApiKey { ProjectId = projectId, Name = "k", KeyPrefix = prefix, KeyHash = hash });
            await seed.SaveChangesAsync();
        }

        await using var conn = await OpenAsAsync(AppUser);
        await SetTenantAsync(conn, null);

        // Tanpa tenant diketahui: baca langsung ke api_keys tertutup (fail closed).
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM dbo.api_keys WHERE key_prefix = @p;", ("@p", prefix))));

        await AssertBootstrapRowAsync(conn, prefix, expectedTenant: a.Id, expectedProject: projectId, expectedHash: hash);

        // Bahkan dengan context tenant lain, key tenant lain hanya terbaca lewat prosedur bootstrap.
        await SetTenantAsync(conn, b.Id);
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM dbo.api_keys WHERE key_prefix = @p;", ("@p", prefix))));
        await AssertBootstrapRowAsync(conn, prefix, expectedTenant: a.Id, expectedProject: projectId, expectedHash: hash);

        await AssertBootstrapEmptyAsync(conn, "gw_tidakada");
    }

    [Fact]
    public async Task Audit_logs_are_append_only_for_app_and_deletable_for_platform()
    {
        await PrepareAsync();
        var a = await testDb.NewTenantAsync();
        var b = await testDb.NewTenantAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];

        await using (var seed = await OpenAsAsync(PlatformUser))
        {
            await ExecAsync(seed, "INSERT INTO dbo.audit_logs (tenant_id, action) VALUES (@t, @a);", ("@t", a.Id), ("@a", $"seed-a-{tag}"));
            await ExecAsync(seed, "INSERT INTO dbo.audit_logs (tenant_id, action) VALUES (@t, @a);", ("@t", b.Id), ("@a", $"seed-b-{tag}"));
            await ExecAsync(seed, "INSERT INTO dbo.audit_logs (tenant_id, action) VALUES (NULL, @a);", ("@a", $"platform-{tag}"));
        }

        await using var app = await OpenAsAsync(AppUser);
        await SetTenantAsync(app, a.Id);

        // Block predicate menolak INSERT lintas tenant.
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(app,
            "INSERT INTO dbo.audit_logs (tenant_id, action) VALUES (@t, N'forged');", ("@t", b.Id)));
        await ExecAsync(app, "INSERT INTO dbo.audit_logs (tenant_id, action) VALUES (@t, @a);", ("@t", a.Id), ("@a", $"app-write-{tag}"));
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(app,
            "SELECT COUNT(*) FROM dbo.audit_logs WHERE action IN (@a, @b);", ("@a", $"seed-a-{tag}"), ("@b", $"app-write-{tag}"))));
        // Baris tingkat platform (tenant_id NULL) tidak terlihat oleh tenant.
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(app,
            "SELECT COUNT(*) FROM dbo.audit_logs WHERE action = @a;", ("@a", $"platform-{tag}"))));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(app,
            "UPDATE dbo.audit_logs SET action = N'ubah' WHERE tenant_id = @t;", ("@t", a.Id)));
        await Assert.ThrowsAsync<SqlException>(() => ExecAsync(app,
            "DELETE FROM dbo.audit_logs WHERE tenant_id = @t;", ("@t", a.Id)));

        // Retensi sebagai principal gateway_platform.
        await using var platform = await OpenAsAsync(PlatformUser);
        Assert.Equal(1, await ExecNonQueryAsync(platform,
            "DELETE FROM dbo.audit_logs WHERE action = @a;", ("@a", $"seed-b-{tag}")));
        Assert.Equal(1, await ExecNonQueryAsync(platform,
            "UPDATE dbo.audit_logs SET ip = N'10.0.0.1' WHERE action = @a;", ("@a", $"seed-a-{tag}")));
    }

    [Fact]
    public async Task Maintenance_tables_follow_the_same_isolation()
    {
        await PrepareAsync();
        await using var admin = new SqlConnection(testDb.ConnectionString);
        await admin.OpenAsync();
        // Migration G4Maintenance ada di repo: tabel ini wajib ada, kalau tidak bagian 4b script tidak
        // memasang policy-nya dan uji perilaku di bawah tidak bermakna.
        Assert.True(await TableExistsAsync(admin, "request_bodies"),
            "request_bodies tidak ada; migration G4Maintenance tidak diterapkan.");

        var a = await testDb.NewTenantAsync();
        var b = await testDb.NewTenantAsync();
        var firstId = Random.Shared.NextInt64(900_000_000_000L, 999_999_999_999L);
        foreach (var (tenantId, usageLogId) in new[] { (a.Id, firstId), (b.Id, firstId + 1) })
        {
            await SetTenantAsync(admin, tenantId);
            await ExecAsync(admin, """
                INSERT INTO dbo.request_bodies (usage_log_id, tenant_id, request_json, response_json, expires_at)
                VALUES (@id, @t, N'{}', N'{}', DATEADD(DAY, 1, SYSUTCDATETIME()));
                """, ("@id", usageLogId), ("@t", tenantId));
        }

        await using var app = await OpenAsAsync(AppUser);
        await SetTenantAsync(app, null);
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(app,
            "SELECT COUNT(*) FROM dbo.request_bodies WHERE tenant_id IN (@a, @b);", ("@a", a.Id), ("@b", b.Id))));
        await SetTenantAsync(app, a.Id);
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(app,
            "SELECT COUNT(*) FROM dbo.request_bodies WHERE tenant_id IN (@a, @b);", ("@a", a.Id), ("@b", b.Id))));
        Assert.Equal(0, await ExecNonQueryAsync(app,
            "DELETE FROM dbo.request_bodies WHERE tenant_id = @t;", ("@t", b.Id)));

        // Retensi hapus lintas tenant hanya lewat principal gateway_platform.
        await using var platform = await OpenAsAsync(PlatformUser);
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(platform,
            "SELECT COUNT(*) FROM dbo.request_bodies WHERE tenant_id IN (@a, @b);", ("@a", a.Id), ("@b", b.Id))));
        Assert.Equal(1, await ExecNonQueryAsync(platform,
            "DELETE FROM dbo.request_bodies WHERE tenant_id = @t;", ("@t", b.Id)));
    }

    [Fact]
    public async Task Interceptor_reapplies_context_on_open_connection_and_blocks_query_filter_bypass()
    {
        await PrepareAsync();
        var (a, b) = await SeedTenantsAsync();

        await using var conn = await OpenAsAsync(AppUser); // koneksi TETAP terbuka selama test
        await using var ctx = NewRlsContext(conn, a.Id);

        Assert.Equal(a.Id, Assert.Single(await ctx.Providers.AsNoTracking().ToListAsync()).TenantId);
        Assert.Equal(a.Id, await SessionTenantAsync(conn));

        ctx.CurrentTenantId = b.Id; // tenant berganti, koneksi tidak pernah ditutup
        Assert.Equal(b.Id, Assert.Single(await ctx.Providers.AsNoTracking().ToListAsync()).TenantId);
        Assert.Equal(b.Id, await SessionTenantAsync(conn));

        // IgnoreQueryFilters melewatkan filter EF, tetapi RLS tetap membatasi di database.
        Assert.Equal(b.Id, Assert.Single(await ctx.Providers.IgnoreQueryFilters().AsNoTracking().ToListAsync()).TenantId);

        ctx.CurrentTenantId = GatewayDbContext.NoTenant;
        Assert.Empty(await ctx.Providers.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        Assert.Null(await SessionTenantAsync(conn));
    }

    [Fact]
    public async Task Pooled_connections_never_leak_the_previous_tenant()
    {
        await PrepareAsync();
        var (a, b) = await SeedTenantsAsync();

        await using (var connA = await OpenAsAsync(AppUser))
        {
            await using var ctxA = NewRlsContext(connA, a.Id);
            var rows = await ctxA.Providers.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            Assert.Equal(a.Id, Assert.Single(rows).TenantId);
            Assert.Equal(a.Id, await SessionTenantAsync(connA));
        }

        // Koneksi logis baru (fisik bisa berasal dari pool yang sama) untuk tenant lain.
        await using (var connB = await OpenAsAsync(AppUser))
        {
            await using var ctxB = NewRlsContext(connB, b.Id);
            var rows = await ctxB.Providers.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            Assert.Equal(b.Id, Assert.Single(rows).TenantId);
            Assert.Equal(b.Id, await SessionTenantAsync(connB));
        }
    }

    private static GatewayDbContext NewRlsContext(DbConnection connection, long tenantId)
    {
        var interceptor = new TenantSessionInterceptor();
        var builder = new DbContextOptionsBuilder<GatewayDbContext>();
        builder.UseSqlServer(connection);
        builder.AddInterceptors(interceptor);
        return new GatewayDbContext(builder.Options, interceptor) { CurrentTenantId = tenantId };
    }

    private async Task<(Tenant A, Tenant B)> SeedTenantsAsync()
    {
        var a = await testDb.NewTenantAsync();
        var b = await testDb.NewTenantAsync();
        await SeedProviderAsync(a.Id, "pa");
        await SeedProviderAsync(b.Id, "pb");
        return (a, b);
    }

    private async Task SeedProviderAsync(long tenantId, string name)
    {
        await using var ctx = testDb.NewContext(tenantId);
        ctx.Providers.Add(new Provider { Name = name, BaseUrl = "https://provider.test/v1/" });
        await ctx.SaveChangesAsync();
    }

    private async Task<long> SeedProjectAsync(long tenantId)
    {
        await using var ctx = testDb.NewContext(tenantId);
        var project = new Project { Name = $"p-{Guid.NewGuid():N}" };
        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task<DbConnection> OpenAsAsync(string user)
    {
        // EXECUTE AS USER tidak di-revert saat koneksi dikembalikan ke pool: konteks impersonasi akan
        // diwarisi koneksi berikutnya (dan sesi pooled itu berakhir "kill state"). Koneksi uji ini
        // sengaja tidak di-pool.
        var conn = new SqlConnection(testDb.ConnectionString + ";Pooling=false");
        await conn.OpenAsync();
        await ExecAsync(conn, $"EXECUTE AS USER = N'{user}';");
        return conn;
    }

    private static Task SetTenantAsync(DbConnection conn, long? tenantId) =>
        ExecAsync(conn, "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @value;", ("@value", tenantId));

    private static async Task<long?> SessionTenantAsync(DbConnection conn)
    {
        var value = await ScalarAsync(conn, "SELECT SESSION_CONTEXT(N'tenant_id');");
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<int> ProviderCountAsync(DbConnection conn) =>
        Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.providers;"));

    private static async Task<string> ProviderSummaryAsync(DbConnection conn)
    {
        var value = await ScalarAsync(conn,
            "SELECT CAST(MIN(tenant_id) AS NVARCHAR(20)) + ':' + CAST(COUNT(*) AS NVARCHAR(20)) FROM dbo.providers;");
        return value?.ToString() ?? "";
    }

    private static async Task AssertBootstrapRowAsync(
        DbConnection conn, string prefix, long expectedTenant, long expectedProject, string expectedHash)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXEC dbo.api_key_bootstrap @key_prefix = @p;";
        AddParameters(cmd, [("@p", prefix)]);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedTenant, reader.GetInt64(reader.GetOrdinal("tenant_id")));
        Assert.Equal(expectedProject, reader.GetInt64(reader.GetOrdinal("project_id")));
        Assert.Equal(prefix, reader.GetString(reader.GetOrdinal("key_prefix")));
        Assert.Equal(expectedHash, reader.GetString(reader.GetOrdinal("key_hash")));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AssertBootstrapEmptyAsync(DbConnection conn, string prefix)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXEC dbo.api_key_bootstrap @key_prefix = @p;";
        AddParameters(cmd, [("@p", prefix)]);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<bool> TableExistsAsync(DbConnection conn, string name) =>
        await ScalarAsync(conn, "SELECT OBJECT_ID(N'dbo.' + @name, N'U');", ("@name", name)) is not null and not DBNull;

    /// <summary>Jumlah predicate per tabel sasaran untuk kedua security policy RLS.</summary>
    private static async Task<Dictionary<string, int>> PredicateCountsAsync(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name, COUNT(*) FROM sys.security_predicates p
            JOIN sys.security_policies s ON s.object_id = p.object_id
            JOIN sys.tables t ON t.object_id = p.target_object_id
            WHERE s.name IN (N'tenant_isolation_policy', N'maintenance_isolation_policy')
            GROUP BY t.name;
            """;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private static async Task ExecAsync(DbConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, args);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecNonQueryAsync(DbConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(DbConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, args);
        return await cmd.ExecuteScalarAsync();
    }

    private static void AddParameters(DbCommand cmd, (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
    }
}
