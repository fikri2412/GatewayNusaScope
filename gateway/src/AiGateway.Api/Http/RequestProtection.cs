using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
// System.Net.IPNetwork juga ada sejak .NET 8; KnownNetworks memakai tipe HttpOverrides.
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace AiGateway.Api.Http;

public sealed class RequestProtectionOptions
{
    public const string Section = "Security";
    [Range(1, 1_000_000)] public int AuthRequestsPerMinute { get; set; } = 20;
    [Range(1024, 64 * 1024 * 1024)] public int MaxAdminRequestBytes { get; set; } = 1024 * 1024;
    /// <summary>
    /// Proxy tepercaya untuk header X-Forwarded-*: IP atau CIDR. Kosong = hanya loopback (default ASP.NET Core).
    /// Isi hanya dengan proxy milik sendiri; entri tidak valid menggagalkan startup.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];
}

public static class RequestProtection
{
    public const string AuthPolicy = "auth-ip";
    private static readonly Meter Meter = new("AiGateway.Http", "1.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("gateway.http.requests");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gateway.http.duration", "ms");

    /// <summary>
    /// Opsi header X-Forwarded-*: <c>XForwardedFor | XForwardedProto</c>, satu hop, dan hanya dari proxy tepercaya —
    /// default ASP.NET Core (loopback) dipertahankan, ditambah <c>Security:TrustedProxies</c> (IP masuk ke
    /// <see cref="ForwardedHeadersOptions.KnownProxies"/>, CIDR ke <see cref="ForwardedHeadersOptions.KnownNetworks"/>).
    /// Entri tidak valid melempar <see cref="InvalidOperationException"/> (pola <c>Security:AllowedPrivateNetworks</c>).
    /// </summary>
    public static ForwardedHeadersOptions BuildForwardedHeadersOptions(IReadOnlyList<string>? trustedProxies)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        var errors = new List<string>();
        foreach (var raw in trustedProxies ?? [])
        {
            var entry = raw?.Trim() ?? "";
            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out var network)) { options.KnownNetworks.Add(network); continue; }
            }
            else if (entry.Length > 0 && IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
                continue;
            }
            errors.Add($"'{raw}' bukan IP atau CIDR yang valid.");
        }
        if (errors.Count > 0)
            throw new InvalidOperationException("Security:TrustedProxies tidak valid: " + string.Join(" ", errors));
        return options;
    }

    /// <summary>
    /// Middleware pertama pipeline: ganti IP/skema klien dengan nilai X-Forwarded-* hanya bila peer-nya
    /// proxy tepercaya (loopback + <c>Security:TrustedProxies</c>); header dari peer lain diabaikan.
    /// </summary>
    public static IApplicationBuilder UseTrustedForwardedHeaders(this IApplicationBuilder app)
    {
        var proxies = app.ApplicationServices.GetRequiredService<IOptions<RequestProtectionOptions>>().Value.TrustedProxies;
        return app.UseForwardedHeaders(BuildForwardedHeadersOptions(proxies));
    }

    public static IServiceCollection AddRequestProtection(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<RequestProtectionOptions>().Bind(config.GetSection(RequestProtectionOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        // Entri proxy tepercaya diparse saat komposisi: salah konfigurasi gagal saat start dengan pesan per entri.
        BuildForwardedHeadersOptions(config.GetSection(RequestProtectionOptions.Section)
            .GetSection(nameof(RequestProtectionOptions.TrustedProxies)).Get<string[]>());
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(AuthPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IOptions<RequestProtectionOptions>>().Value.AuthRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
                }));
            options.OnRejected = async (context, ct) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var time) ? Math.Max(1, (int)Math.Ceiling(time.TotalSeconds)) : 60;
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = new
                {
                    message = "Terlalu banyak permintaan autentikasi. Coba lagi nanti.", type = "rate_limit_error", param = (string?)null, code = "auth_rate_limited",
                } }, ct);
            };
        });
        return services;
    }

    public static IApplicationBuilder UseRequestProtection(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["X-Frame-Options"] = "DENY";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        if (http.Request.Path.StartsWithSegments("/admin/api") || http.Request.Path.StartsWithSegments("/platform/api"))
        {
            http.Response.Headers.CacheControl = "no-store";
            var limit = http.RequestServices.GetRequiredService<IOptions<RequestProtectionOptions>>().Value.MaxAdminRequestBytes;
            if (http.Request.ContentLength > limit) throw new BadHttpRequestException("Body terlalu besar.", 413);
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = limit;
        }
        var started = Stopwatch.GetTimestamp();
        try { await next(); }
        finally
        {
            // Tidak melabeli tenant, URI mentah, token, atau email: cardinality metrik tetap terbatas.
            var area = http.Request.Path.StartsWithSegments("/v1") ? "data" :
                http.Request.Path.StartsWithSegments("/platform/api") ? "platform" :
                http.Request.Path.StartsWithSegments("/admin/api") ? "admin" : "web";
            Requests.Add(1, new KeyValuePair<string, object?>("area", area), new("status", http.Response.StatusCode));
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("area", area));
        }
    });
}
