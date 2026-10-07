using AiGateway.Api.Auth;
using AiGateway.Api.Dev;
using AiGateway.Api.Endpoints;
using AiGateway.Api.Http;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Options;
using AiGateway.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddGatewayCore(builder.Configuration);
builder.Services.AddGatewayAuth();
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddRequestProtection(builder.Configuration);
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

app.MapDataPlane();
app.MapAdminAuth();
app.MapAdminProviders();
app.MapAdminModels();
app.MapAdminResources();
app.MapPlatformManagement();
app.MapMaintenance();

// Path API yang tidak dikenal → 404 JSON; selain itu layani UI (SPA) bila sudah di-build.
app.MapFallback(async http =>
{
    var path = http.Request.Path;
    if (path.StartsWithSegments("/admin/api") || path.StartsWithSegments("/platform/api") || path.StartsWithSegments("/v1"))
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
