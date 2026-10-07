using AiGateway.Core.Catalog;
using AiGateway.Core.Data;
using AiGateway.Core.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Dev;

/// <summary>
/// Hanya Development: bila <c>DevSeed:UpstreamApiKey</c> (user-secrets) diisi, buat tenant "dev" berisi provider
/// OpenCode, model dari katalog, project, dan satu API key gateway (ditampilkan sekali di log).
/// Idempoten: tenant "dev" yang sudah ada tidak disentuh. Digantikan UI admin di G3.
/// </summary>
public static class DevSeeder
{
    public const string TenantSlug = "dev";

    public static async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var upstreamKey = config["DevSeed:UpstreamApiKey"];
        if (string.IsNullOrWhiteSpace(upstreamKey)) return;

        var db = services.GetRequiredService<GatewayDbContext>();
        if (await db.Tenants.AnyAsync(t => t.Slug == TenantSlug, ct)) return;

        var log = services.GetRequiredService<ILogger<GatewayDbContext>>();
        var provisioning = services.GetRequiredService<ProvisioningService>();
        var sync = await services.GetRequiredService<OpenCodeCatalogSync>().SyncAsync(upstreamKey, DbSeeder.OpenCodeConfigUrl, ct);
        log.LogInformation("DEV SEED: katalog OpenCode disinkronkan: {Result}", sync);

        var tenant = await provisioning.CreateTenantAsync("Dev Tenant", TenantSlug, null, ct);
        db.CurrentTenantId = tenant.Id;
        var provider = await provisioning.CreateProviderFromTemplateAsync(
            DbSeeder.OpenCodeTemplateCode, "opencode", upstreamKey, null, ct);
        await provisioning.ImportModelsAsync(provider.Id, await provisioning.CatalogSpecsAsync(provider.Id, null, ct), ct);
        var project = await provisioning.CreateProjectAsync("dev", ct);
        var key = await provisioning.CreateApiKeyAsync(project.Id, "dev-key", null, null, null, ct);

        log.LogWarning("DEV SEED: tenant '{Slug}' dibuat. API key gateway (tampil sekali): {Key}", TenantSlug, key.PlaintextKey);
    }
}
