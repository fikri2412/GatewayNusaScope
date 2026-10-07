using System.ComponentModel.DataAnnotations;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AiGateway.Core.Audit;
using AiGateway.Core.Auth;
using AiGateway.Core.Catalog;
using AiGateway.Core.Reports;
using Microsoft.Extensions.Options;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using AiGateway.Core.Security;
using AiGateway.Core.Maintenance;

namespace AiGateway.Core.Options;

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    /// <summary>Jalankan migration saat startup. Nyalakan di dev; di produksi jalankan migration terkontrol.</summary>
    public bool AutoMigrate { get; set; }
}

/// <summary>Akun platform admin pertama. Password hanya lewat user-secrets atau variabel lingkungan.</summary>
public sealed class SeedOptions
{
    public const string Section = "Seed";
    [EmailAddress] public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
}

public sealed class ProxyOptions
{
    public const string Section = "Proxy";
    [Range(1024, 64 * 1024 * 1024)] public int MaxRequestBytes { get; set; } = 4 * 1024 * 1024;
    [Range(1, 600)] public int UpstreamTimeoutSeconds { get; set; } = 120;
    /// <summary>
    /// Batas durasi total satu stream SSE. Batas diam antar baris memakai <see cref="UpstreamTimeoutSeconds"/>; batas
    /// total mencegah upstream yang mengirim sepotong demi sepotong menahan koneksi dan slot tenant tanpa akhir.
    /// </summary>
    [Range(1, 86400)] public int MaxStreamSeconds { get; set; } = 900;
    /// <summary>Batas body respons upstream (provider milik tenant tidak dipercaya; tanpa batas, satu tenant bisa menghabiskan memori proses).</summary>
    [Range(1024, 256 * 1024 * 1024)] public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
    /// <summary>Permintaan chat yang boleh berjalan bersamaan per tenant; sisanya ditolak 429.</summary>
    [Range(1, 10_000)] public int MaxConcurrentPerTenant { get; set; } = 64;
}

/// <summary>Kebijakan login admin: kunci akun sementara, umur sesi, panjang password.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";
    [Range(1, 20)] public int MaxFailedLogins { get; set; } = 5;
    [Range(1, 1440)] public int LockoutMinutes { get; set; } = 15;
    [Range(1, 1440)] public int AccessTokenMinutes { get; set; } = 15;
    [Range(1, 365)] public int RefreshTokenDays { get; set; } = 14;
    [Range(8, 128)] public int MinPasswordLength { get; set; } = 12;
}

/// <summary>Penandatangan access token admin. SigningKey adalah secret (user-secrets / variabel lingkungan).</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";
    [Required, MinLength(32)] public string SigningKey { get; set; } = "";
    [Required] public string Issuer { get; set; } = "ai-gateway";
    [Required] public string Audience { get; set; } = "ai-gateway-admin";
}

public static class GatewayServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayCore(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Gateway")
            ?? throw new InvalidOperationException("ConnectionStrings:Gateway belum diisi.");
        services.AddScoped<TenantSessionInterceptor>();
        services.AddDbContext<GatewayDbContext>((sp, o) => o.UseGatewaySqlServer(cs)
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));

        services.AddOptions<DatabaseOptions>().Bind(config.GetSection(DatabaseOptions.Section)).ValidateOnStart();
        services.AddOptions<SeedOptions>().Bind(config.GetSection(SeedOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => string.IsNullOrWhiteSpace(o.AdminEmail) || o.AdminPassword is { Length: >= 12 },
                "Seed:AdminPassword wajib diisi minimal 12 karakter bila Seed:AdminEmail diisi.")
            .ValidateOnStart();

        services.AddOptions<ProxyOptions>().Bind(config.GetSection(ProxyOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthOptions>().Bind(config.GetSection(AuthOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        // Upstream: tanpa redirect (anti-SSRF); timeout dari konfigurasi. Retry/fallback diatur ChatProxy, bukan handler.
        services.AddHttpClient(ChatProxy.HttpClientName, (sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ProxyOptions>>().Value.UpstreamTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(OutboundSecurity.CreateHandler);

        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<DbSeeder>();
        services.AddScoped<ProviderKeyProtector>();
        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<UsageRecorder>();
        services.AddScoped<ChatProxy>();
        services.AddScoped<ProvisioningService>();
        services.AddScoped<PolicyEngine>();
        services.AddSingleton<RequestRateLimiter>();
        services.AddSingleton<TenantConcurrencyGate>();
        services.AddOutboundSecurity(config);
        services.AddScoped<PlatformService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OpenCodeCatalogSync>();
        services.AddGatewayMaintenance(config);
        services.AddHttpClient(WebhookDeliveryService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(OutboundSecurity.CreateHandler);
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<CurrentActor>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<UsageReports>();
        services.AddScoped<PublicIdResolver>();
        return services;
    }
}
