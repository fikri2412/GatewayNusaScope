using AiGateway.Core.Data;
using AiGateway.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Maintenance;

/// <summary>
/// Sumber scope untuk job maintenance. Bila <c>ConnectionStrings:GatewayPlatform</c> diisi, job berjalan di
/// kontainer terpisah dengan koneksi principal <c>gateway_platform</c> — satu-satunya cara membaca/menghapus
/// lintas tenant saat RLS aktif, karena bypass RLS berbasis keanggotaan role database
/// (<c>IS_MEMBER(N'gateway_platform')</c>), bukan <c>IgnoreQueryFilters()</c> atau flag sesi.
/// Tanpa connection string itu job memakai scope aplikasi biasa (dev/test, atau RLS belum dipasang) dan
/// sebuah peringatan dicatat saat start, karena pada environment ber-RLS job tersebut menjadi no-op.
/// </summary>
public sealed class MaintenanceScopeFactory : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ServiceProvider? _platform;

    public MaintenanceScopeFactory(IServiceProvider root, string? platformConnectionString)
    {
        if (string.IsNullOrWhiteSpace(platformConnectionString))
        {
            // Satu peringatan saat start: tanpa koneksi platform, job lintas tenant berjalan sebagai principal
            // aplikasi dan tidak melakukan apa pun (diam-diam) begitu RLS SQL Server dipasang.
            root.GetRequiredService<ILoggerFactory>().CreateLogger<MaintenanceScopeFactory>().LogWarning(
                "ConnectionStrings:GatewayPlatform tidak diisi; retensi dan pengiriman webhook memakai koneksi aplikasi " +
                "dan akan no-op tanpa peringatan bila RLS SQL Server aktif.");
            _scopes = root.GetRequiredService<IServiceScopeFactory>();
            return;
        }
        string connectionString = platformConnectionString;

        // Opsi, waktu, logging, kebijakan keluar, dan Data Protection diambil dari kontainer akar supaya
        // validasi/opsi sama; DbContext memakai koneksi platform dan interceptor sesi (pola yang sama
        // dengan konteks aplikasi), klien webhook memakai handler anti-SSRF yang sama dengan kontainer akar.
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(root.GetRequiredService<IOptions<MaintenanceOptions>>());
        services.AddSingleton(root.GetRequiredService<TimeProvider>());
        services.AddSingleton(root.GetRequiredService<ILoggerFactory>());
        services.AddSingleton(root.GetRequiredService<IDataProtectionProvider>());
        services.AddSingleton(root.GetRequiredService<OutboundSecurityPolicy>());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddScoped<TenantSessionInterceptor>();
        services.AddDbContext<GatewayDbContext>((sp, o) => o.UseGatewaySqlServer(connectionString)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));
        services.AddHttpClient(WebhookDeliveryService.HttpClientName, (sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<MaintenanceOptions>>().Value.WebhookTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(OutboundSecurity.CreateHandler);
        services.AddScoped<WebhookSecretProtector>();
        services.AddScoped<RetentionService>();
        services.AddScoped<AlertService>();
        services.AddScoped<WebhookDeliveryService>();
        services.AddScoped<JobRunRecorder>();

        // ValidateOnBuild: registrasi yang kurang (mis. klien/handler webhook) gagal saat start, bukan tiap siklus.
        _platform = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _scopes = _platform.GetRequiredService<IServiceScopeFactory>();
    }

    public AsyncServiceScope CreateAsyncScope() => _scopes.CreateAsyncScope();

    public void Dispose() => _platform?.Dispose();
}
