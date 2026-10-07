using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>Database LocalDB sementara per test class, dibuat lewat migration sungguhan lalu dihapus.</summary>
public sealed class TestDb : IAsyncLifetime
{
    public string ConnectionString { get; } =
        $"Server=(localdb)\\MSSQLLocalDB;Database=AiGateway_Test_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    public GatewayDbContext NewContext(long tenantId = GatewayDbContext.NoTenant)
    {
        var builder = new DbContextOptionsBuilder<GatewayDbContext>();
        var rls = new TenantSessionInterceptor { TenantId = tenantId };
        builder.UseGatewaySqlServer(ConnectionString).AddInterceptors(rls);
        return new GatewayDbContext(builder.Options, rls) { CurrentTenantId = tenantId };
    }

    /// <summary>Plan baru + tenant baru dengan slug unik.</summary>
    public async Task<Tenant> NewTenantAsync()
    {
        await using var db = NewContext();
        var plan = new Plan { Name = $"plan-{Guid.NewGuid():N}" };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        var id = Guid.NewGuid().ToString("N");
        var tenant = new Tenant { Name = id, Slug = id, PlanId = plan.Id };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>
    /// Pasang lapis kedua (script RLS) di database ini dengan koneksi pemilik database. Idempoten, jadi aman
    /// dipanggil berulang; hanya database test ini yang tersentuh. Provider SqlClient sinkron supaya bisa
    /// dipanggil dari konstruktor factory sebelum host start (tanpa deadlock context async xunit).
    /// </summary>
    public void InstallTenantSecurity() => InstallTenantSecurityScript(ConnectionString);

    /// <summary>Jalankan <c>gateway/sql/tenant-security.sql</c> pada connection string yang diberikan.</summary>
    public static void InstallTenantSecurityScript(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = File.ReadAllText(FindTenantSecurityScript());
        cmd.CommandTimeout = 120;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Cari script: salinan di folder output (csproj: <c>..\..\sql\tenant-security.sql</c> dengan
    /// CopyToOutputDirectory) atau, bila belum disalin, naik dari direktori bin sampai ketemu
    /// <c>gateway/sql</c>.
    /// </summary>
    private static string FindTenantSecurityScript()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "sql", "tenant-security.sql"),
                     Path.Combine(AppContext.BaseDirectory, "tenant-security.sql"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "gateway", "sql", "tenant-security.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"gateway/sql/tenant-security.sql tidak ditemukan dari {AppContext.BaseDirectory}");
    }

    private static readonly Lazy<Task> SweepOnce = new(SweepStaleAsync);

    public async Task InitializeAsync()
    {
        await SweepOnce.Value;
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Run yang dimatikan paksa (Ctrl+C, timeout) tidak pernah sampai <see cref="DisposeAsync"/> dan meninggalkan
    /// database di LocalDB. Sekali per proses test, buang database uji berumur lebih dari satu jam; batas umur menjaga
    /// database milik run paralel yang sedang berjalan. Best-effort: gagal bersih-bersih tidak boleh menggagalkan suite.
    /// </summary>
    private static async Task SweepStaleAsync()
    {
        try
        {
            await using var conn = new SqlConnection(
                "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(name) + N';'
                FROM sys.databases
                WHERE name LIKE N'AiGateway[_]Test[_]%' AND create_date < DATEADD(HOUR, -1, GETDATE());
                EXEC(@sql);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
        }
    }

    public async Task DisposeAsync()
    {
        // Koneksi yang masih ada di pool setelah database di-drop tetap "kill state"; buang pool database ini
        // saja (ClearAllPools bersifat proses-wide dan bisa memutus test class lain yang berjalan paralel).
        SqlConnection.ClearPool(new SqlConnection(ConnectionString));
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }
}
