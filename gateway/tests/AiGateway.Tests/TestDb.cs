using AiGateway.Core.Data;
using AiGateway.Core.Domain;
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

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        // Koneksi yang masih ada di pool setelah database di-drop tetap "kill state"; buang pool database ini
        // saja (ClearAllPools bersifat proses-wide dan bisa memutus test class lain yang berjalan paralel).
        Microsoft.Data.SqlClient.SqlConnection.ClearPool(new Microsoft.Data.SqlClient.SqlConnection(ConnectionString));
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }
}
