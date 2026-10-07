using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.RateLimiting;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AiGateway.Api.Http;

public sealed class RequestProtectionOptions
{
    public const string Section = "Security";
    [Range(1, 1_000_000)] public int AuthRequestsPerMinute { get; set; } = 20;
    [Range(1024, 64 * 1024 * 1024)] public int MaxAdminRequestBytes { get; set; } = 1024 * 1024;
}

public static class RequestProtection
{
    public const string AuthPolicy = "auth-ip";
    private static readonly Meter Meter = new("AiGateway.Http", "1.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("gateway.http.requests");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gateway.http.duration", "ms");

    public static IServiceCollection AddRequestProtection(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<RequestProtectionOptions>().Bind(config.GetSection(RequestProtectionOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
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
