using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Data;

/// <summary>
/// Koneksi principal <c>gateway_platform</c> (<c>ConnectionStrings:GatewayPlatform</c>) untuk dua pekerjaan request
/// HTTP yang harus melewati RLS: laporan lintas tenant di plane platform dan baris audit tanpa tenant (aksi
/// platform, login platform admin, login gagal, accept-invite). Hanya kode itu yang memakainya; endpoint lain
/// tetap di koneksi aplikasi, jadi bypass RLS tidak ikut terbawa ke jalur tenant.
/// Tanpa connection string itu (dev/test, atau RLS belum dipasang) <see cref="IsSeparate"/> false dan pemanggil
/// memakai konteks request seperti semula.
/// </summary>
public class PlatformDb(string? connectionString)
{
    public bool IsSeparate => !string.IsNullOrWhiteSpace(connectionString);

    /// <summary>Konteks baru di koneksi platform tanpa tenant; pemanggil membuangnya. Hanya bila <see cref="IsSeparate"/>.</summary>
    public GatewayDbContext CreateContext()
    {
        var session = new TenantSessionInterceptor();
        var options = new DbContextOptionsBuilder<GatewayDbContext>();
        options.UseGatewaySqlServer(connectionString!).AddInterceptors(session);
        Configure(options);
        return new GatewayDbContext(options.Options, session);
    }

    /// <summary>Jalankan <paramref name="work"/> di konteks platform, atau di <paramref name="requestDb"/> bila koneksi platform tidak diisi.</summary>
    public async Task<T> RunAsync<T>(GatewayDbContext requestDb, Func<GatewayDbContext, Task<T>> work)
    {
        if (!IsSeparate) return await work(requestDb);
        await using var platform = CreateContext();
        return await work(platform);
    }

    /// <summary>Titik pasang test: principal uji bukan anggota <c>gateway_platform</c> sungguhan, jadi test menyamarkannya.</summary>
    protected virtual void Configure(DbContextOptionsBuilder<GatewayDbContext> options) { }
}
