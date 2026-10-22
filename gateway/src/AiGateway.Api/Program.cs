using AiGateway.Api.Auth;
using AiGateway.Api.Dev;
using AiGateway.Api.Endpoints;
using AiGateway.Api.Http;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Maintenance;
using AiGateway.Core.Options;
using AiGateway.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
// Lifetime Windows Service (SCM) saat host dijalankan lewat scripts/install-service.ps1; no-op di luar service.
builder.Host.UseWindowsService();

builder.Services.AddOpenApi();
builder.Services.AddGatewayCore(builder.Configuration);
builder.Services.AddGatewayAuth();
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddRequestProtection(builder.Configuration);
builder.Services.AddGatewayMetrics(builder.Configuration); // GB-05: nonaktif kecuali Metrics:Enabled=true (+ token)
// Kunci Data Protection (dipakai mengenkripsi key provider) disimpan di database, dienkripsi
// DPAPI (Windows) atau sertifikat (Security:KeyCertificateThumbprint) sebelum disimpan.
builder.Services.AddProtectedGatewayKeys(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    if (sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.AutoMigrate)
        await sp.GetRequiredService<GatewayDbContext>().Database.MigrateAsync();
    await sp.GetRequiredService<DbSeeder>().RunAsync(app.Lifetime.ApplicationStopping);
    if (app.Environment.IsDevelopment())
        await DevSeeder.RunAsync(sp, app.Lifetime.ApplicationStopping);
}

// Paling awal: pakai IP/skema klien asli dari X-Forwarded-* (hanya dari proxy tepercaya) supaya rate limit login,
// audit, allowed_ips, dan usage_logs.client_ip melihat IP pemanggil, bukan IP reverse proxy.
app.UseTrustedForwardedHeaders();
app.UseExceptionHandler(_ => { }); // jawaban dibentuk oleh GatewayExceptionHandler
app.UseRequestProtection();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<RequestContextMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/healthz", async (GatewayDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));

// GB-05: liveness (proses hidup, tanpa dependensi) dan readiness (database, migration, worker maintenance). Tanpa auth,
// tanpa detail galat; kegagalan provider upstream sengaja bukan bagian readiness.
app.MapGet("/healthz/live", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { status = "live" });
});
app.MapGet("/healthz/ready", async (HttpContext http, ReadinessService readiness, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    var report = await readiness.CheckAsync(ct);
    return report.Ready
        ? Results.Ok(new { status = "ready", checks = report.Checks })
        : Results.Json(new { status = "not_ready", checks = report.Checks }, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGatewayMetrics();

app.MapDataPlane();
app.MapAdminAuth();
app.MapAdminProviders();
app.MapAdminModels();
app.MapAdminResources();
app.MapPlatformManagement();
app.MapMaintenance();
app.MapSettings();
app.MapSearchProviders();

// Path API yang tidak dikenal → 404 JSON; selain itu layani UI (SPA) bila sudah di-build.
app.MapFallback(async http =>
{
    var path = http.Request.Path;
    if (path.StartsWithSegments("/admin/api") || path.StartsWithSegments("/admin/providers") || path.StartsWithSegments("/platform/api") || path.StartsWithSegments("/client/api") || path.StartsWithSegments("/v1"))
        throw GatewayException.NotFound("Endpoint");
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    http.Response.ContentType = "text/html; charset=utf-8";
    await http.Response.SendFileAsync(index);
});

app.Run();

public partial class Program;
