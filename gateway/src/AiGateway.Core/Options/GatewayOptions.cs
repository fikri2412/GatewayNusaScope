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
using AiGateway.Core.Settings;
using AiGateway.Core.AppStatus;
using AiGateway.Core.WebSearch;
using Polly;

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
    /// <summary>Deadline satu route sampai headers/body REST atau event SSE pertama; sesudah commit SSE menjadi batas idle.</summary>
    [Range(1, 600)] public int UpstreamTimeoutSeconds { get; set; } = 120;
    /// <summary>
    /// Batas durasi total pompa SSE, termasuk read, write, flush dan error terminal. Idle antar baris memakai
    /// <see cref="UpstreamTimeoutSeconds"/>; deadline total juga menghentikan downstream yang macet.
    /// </summary>
    [Range(1, 86400)] public int MaxStreamSeconds { get; set; } = 900;
    /// <summary>Batas body respons upstream (provider milik tenant tidak dipercaya; tanpa batas, satu tenant bisa menghabiskan memori proses).</summary>
    [Range(1024, 256 * 1024 * 1024)] public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
    /// <summary>Permintaan chat yang boleh berjalan bersamaan per tenant; sisanya ditolak 429.</summary>
    [Range(1, 10_000)] public int MaxConcurrentPerTenant { get; set; } = 64;
    /// <summary>
    /// GB-01: batas waktu seluruh rantai route (semua percobaan + fallback) sampai respons REST utuh atau event SSE pertama;
    /// sesudah commit SSE tidak berlaku lagi. Lewat batas → 504 <c>upstream_timeout</c>. Nilai di bawah
    /// <see cref="UpstreamTimeoutSeconds"/> memangkas satu percobaan; naikkan bila UpstreamTimeoutSeconds sengaja &gt; 180.
    /// </summary>
    [Range(1, 3600)] public int RequestDeadlineSeconds { get; set; } = 180;
    /// <summary>GB-01: lama route dilewati setelah gagal berturut-turut. 0 mematikan cooldown (perilaku sebelum GB-01).</summary>
    [Range(0, 3600)] public int CooldownSeconds { get; set; } = 30;
    /// <summary>GB-01: jumlah kegagalan provider berturut-turut (dalam <see cref="CooldownWindowSeconds"/>) yang memicu cooldown.</summary>
    [Range(1, 100)] public int CooldownFailureThreshold { get; set; } = 3;
    /// <summary>GB-01: kegagalan yang lebih tua dari ini tidak lagi dihitung berturut-turut.</summary>
    [Range(1, 3600)] public int CooldownWindowSeconds { get; set; } = 60;
    /// <summary>GB-01: batas atas cooldown yang berasal dari header <c>Retry-After</c> upstream.</summary>
    [Range(1, 86400)] public int CooldownMaxSeconds { get; set; } = 300;
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
        // Koneksi principal gateway_platform untuk laporan lintas tenant dan audit tanpa tenant (lihat PlatformDb).
        services.AddSingleton(new PlatformDb(config.GetConnectionString("GatewayPlatform")));

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
        services.AddSingleton<RouteCooldownTracker>();
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
        services.AddScoped<AppSettingsService>();
        services.AddScoped<AppStatusService>();
        services.AddScoped<PublicIdResolver>();
        services.AddOptions<WebSearchOptions>().Bind(config.GetSection(WebSearchOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.ProbeLeaseSeconds >= o.ProviderTimeoutSeconds + 5,
                "WebSearch:ProbeLeaseSeconds wajib lebih panjang dari ProviderTimeoutSeconds minimal 5 detik.")
            .Validate(o => o.Providers.Length == 0 || !string.IsNullOrWhiteSpace(o.SeedTenantSlug),
                "WebSearch:SeedTenantSlug wajib diisi bila Providers dikonfigurasi.")
            .ValidateOnStart();
        foreach (var type in SearchProviderTypes.All)
            services.AddHttpClient("SearchProvider." + type, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .RemoveAllLoggers()
                .ConfigurePrimaryHttpMessageHandler(OutboundSecurity.CreateHandler)
                .AddResilienceHandler("search-deadline", (pipeline, context) =>
                    pipeline.AddTimeout(TimeSpan.FromSeconds(
                        context.ServiceProvider.GetRequiredService<IOptions<WebSearchOptions>>().Value.ProviderTimeoutSeconds)));
        foreach (var adapterType in typeof(ISearchProvider).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters
                && typeof(ISearchProvider).IsAssignableFrom(type)))
            services.AddSingleton(typeof(ISearchProvider), adapterType);
        services.TryAddSingleton<IRandomProvider, DefaultRandomProvider>();
        services.AddScoped<WebSearchHub>();
        services.AddScoped<SearchProviderRegistry>();
        return services;
    }
}
