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
    IReadOnlyDictionary<long, ProviderCredential> Credentials,
    IReadOnlyDictionary<(long TemplateId, string UpstreamModel), string> RouteFamilies)
{
    /// <summary>
    /// Family API non-chat route ini menurut katalog template provider. null = Chat Completions: provider custom
    /// tanpa template dan model di luar katalog selalu begitu (OpenAI-compatible yang sah).
    /// </summary>
    public string? FamilyOf(ModelRoute route) =>
        Providers.TryGetValue(route.ProviderId, out var provider) && provider.TemplateId is { } templateId
        && RouteFamilies.TryGetValue((templateId, route.UpstreamModel), out var family)
            ? family : null;

    /// <summary>true bila katalog menyatakan family route ini yang tidak bisa diteruskan gateway (mis. <c>google_generate</c>).</summary>
    public bool HasUnsupportedFamily(ModelRoute route) =>
        FamilyOf(route) is { } family && !ApiFamilies.Forwardable.Contains(family);

    /// <summary>Route aktif, provider aktif, credential aktif, dan family yang memang bisa diteruskan.</summary>
    public bool IsForwardable(ModelRoute route) =>
        Providers.ContainsKey(route.ProviderId) && Credentials.ContainsKey(route.ProviderId) && !HasUnsupportedFamily(route);

    /// <summary>True bila model ini punya minimal satu route yang <see cref="IsForwardable"/>.</summary>
    public bool CanForward(long modelId) => Routes.Any(r => r.ModelId == modelId && IsForwardable(r));

    public static async Task<ForwardingTargets> LoadAsync(GatewayDbContext db, List<long> modelIds, CancellationToken ct)
    {
        var routes = await db.ModelRoutes.AsNoTracking().Where(r => modelIds.Contains(r.ModelId) && r.Enabled).ToListAsync(ct);
        var providerIds = routes.Select(r => r.ProviderId).Distinct().ToList();
        var providers = await db.Providers.AsNoTracking()
            .Where(p => providerIds.Contains(p.Id) && p.Enabled).ToDictionaryAsync(p => p.Id, ct);
        var credentials = await db.ProviderCredentials.AsNoTracking()
            .Where(c => providerIds.Contains(c.ProviderId) && c.Status == CredentialStatuses.Active)
            .ToDictionaryAsync(c => c.ProviderId, ct);

        // Family non-chat per route dibaca dari katalog template provider (tanpa kolom baru di route).
        var templateIds = providers.Values.Select(p => p.TemplateId).OfType<long>().Distinct().ToList();
        Dictionary<(long TemplateId, string UpstreamModel), string> families = [];
        if (templateIds.Count > 0)
        {
            var names = routes.Select(r => r.UpstreamModel).Distinct().ToList();
            var rows = await db.CatalogModels.AsNoTracking()
                .Where(c => c.ApiFamily != ApiFamilies.OpenAiChat && templateIds.Contains(c.TemplateId) && names.Contains(c.UpstreamModel))
                .Select(c => new { c.TemplateId, c.UpstreamModel, c.ApiFamily }).ToListAsync(ct);
            families = rows.ToDictionary(c => (c.TemplateId, c.UpstreamModel), c => c.ApiFamily, FamilyKeyComparer.Instance);
        }
        return new ForwardingTargets(routes, providers, credentials, families);
    }

    /// <summary>
    /// Kolasi SQL yang memilih baris katalog tidak membedakan huruf besar/kecil; pencarian di memori harus sama, kalau
    /// tidak route ber-<c>upstream_model</c> berhuruf lain lolos dari penjagaan family (GA-08).
    /// </summary>
    private sealed class FamilyKeyComparer : IEqualityComparer<(long TemplateId, string UpstreamModel)>
    {
        public static readonly FamilyKeyComparer Instance = new();

        public bool Equals((long TemplateId, string UpstreamModel) x, (long TemplateId, string UpstreamModel) y) =>
            x.TemplateId == y.TemplateId && string.Equals(x.UpstreamModel, y.UpstreamModel, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((long TemplateId, string UpstreamModel) key) =>
            HashCode.Combine(key.TemplateId, StringComparer.OrdinalIgnoreCase.GetHashCode(key.UpstreamModel));
    }
}
