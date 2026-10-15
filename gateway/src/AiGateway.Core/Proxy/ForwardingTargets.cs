using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Route dan provider yang benar-benar bisa meneruskan permintaan: route aktif, provider aktif (baris terhapus
/// disaring filter soft delete <see cref="GatewayDbContext"/>), dan credential aktif. Dipakai bersama oleh
/// <see cref="ChatProxy"/> dan endpoint <c>/v1/models</c> supaya "siap dipakai" di daftar tidak pernah berbeda
/// dari eligibilitas pemilihan route saat request.
/// </summary>
public sealed record ForwardingTargets(
    IReadOnlyList<ModelRoute> Routes,
    IReadOnlyDictionary<long, Provider> Providers,
    IReadOnlyDictionary<long, ProviderCredential> Credentials)
{
    /// <summary>True bila model ini punya minimal satu route ke provider aktif ber-credential aktif.</summary>
    public bool CanForward(long modelId) =>
        Routes.Any(r => r.ModelId == modelId && Providers.ContainsKey(r.ProviderId) && Credentials.ContainsKey(r.ProviderId));

    public static async Task<ForwardingTargets> LoadAsync(GatewayDbContext db, List<long> modelIds, CancellationToken ct)
    {
        var routes = await db.ModelRoutes.AsNoTracking().Where(r => modelIds.Contains(r.ModelId) && r.Enabled).ToListAsync(ct);
        var providerIds = routes.Select(r => r.ProviderId).Distinct().ToList();
        var providers = await db.Providers.AsNoTracking()
            .Where(p => providerIds.Contains(p.Id) && p.Enabled).ToDictionaryAsync(p => p.Id, ct);
        var credentials = await db.ProviderCredentials.AsNoTracking()
            .Where(c => providerIds.Contains(c.ProviderId) && c.Status == CredentialStatuses.Active)
            .ToDictionaryAsync(c => c.ProviderId, ct);
        return new ForwardingTargets(routes, providers, credentials);
    }
}
