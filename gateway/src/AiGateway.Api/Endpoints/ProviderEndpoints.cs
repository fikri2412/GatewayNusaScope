using AiGateway.Api.Auth;
using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Endpoints;

public sealed record ProviderDto(
    Guid Id, string Name, string Type, string BaseUrl, string AuthHeader, string AuthPrefix, string? ModelsPath, bool Enabled,
    string? TemplateCode, string? KeyHint, DateTime CreatedAt);

public sealed record CreateProviderRequest(
    string? TemplateCode, string? Name, string? BaseUrl, string? ApiKey, string? AuthHeader, string? AuthPrefix, string? ModelsPath);

public sealed record UpdateProviderRequest(
    string? Name, string? BaseUrl, bool? Enabled, string? AuthHeader, string? AuthPrefix, string? ModelsPath, bool? ClearModelsPath);

public sealed record RotateKeyRequest(string? ApiKey);

/// <param name="Source"><c>catalog</c> (dari katalog template) atau <c>discovered</c> (id model hasil discover).</param>
public sealed record ImportModelsRequest(string? Source, string[]? UpstreamModels);

public sealed record ImportedModel(Guid Id, string Alias);
public sealed record ImportModelsResponse(IReadOnlyList<ImportedModel> Created, int Skipped);

/// <summary>Provider milik tenant: URI + key milik tenant sendiri (BYOK). Key tidak pernah dikembalikan, hanya 4 karakter terakhir.</summary>
public static class ProviderEndpoints
{
    /// <summary>Batas jumlah model per impor; satu permintaan tidak boleh menyisipkan ratusan baris.</summary>
    private const int MaxImportedModels = 500;

    public static IEndpointRouteBuilder MapAdminProviders(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/api/providers");
        g.MapGet("", ListAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapGet("{id:guid}", GetAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPost("", CreateAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPatch("{id:guid}", UpdateAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapDelete("{id:guid}", DeleteAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/rotate-key", RotateAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/discover", DiscoverAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/import-models", ImportAsync).RequireAuthorization(Policies.TenantAdmin);
        return app;
    }

    // Filter dan urutan diterapkan sebelum proyeksi ke DTO: EF tidak bisa menerjemahkan predikat pada properti DTO.
    private static IQueryable<ProviderDto> Query(GatewayDbContext db, Guid? publicId = null) =>
        from p in db.Providers.AsNoTracking()
        where publicId == null || p.PublicId == publicId
        join t in db.ProviderTemplates on p.TemplateId equals t.Id into templates
        from t in templates.DefaultIfEmpty()
        orderby p.Name
        select new ProviderDto(
            p.PublicId, p.Name, p.Type, p.BaseUrl, p.AuthHeader, p.AuthPrefix, p.ModelsPath, p.Enabled,
            t != null ? t.Code : null,
            db.ProviderCredentials.Where(c => c.ProviderId == p.Id && c.Status == CredentialStatuses.Active).Select(c => c.KeyHint).FirstOrDefault(),
            p.CreatedAt);

    private static async Task<IResult> ListAsync(GatewayDbContext db, CancellationToken ct) =>
        Results.Ok(await Query(db).Take(500).ToListAsync(ct));

    private static async Task<IResult> GetAsync(Guid id, GatewayDbContext db, CancellationToken ct) =>
        await Query(db, id).FirstOrDefaultAsync(ct) is { } dto ? Results.Ok(dto) : throw GatewayException.NotFound("Provider");

    private static async Task<IResult> CreateAsync(
        CreateProviderRequest r, GatewayDbContext db, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var provider = !string.IsNullOrWhiteSpace(r.TemplateCode)
            ? await prov.CreateProviderFromTemplateAsync(r.TemplateCode, r.Name ?? r.TemplateCode, r.ApiKey ?? "", r.BaseUrl, ct)
            : await prov.CreateCustomProviderAsync(r.Name ?? "", r.BaseUrl ?? "", r.ApiKey ?? "", r.AuthHeader, r.AuthPrefix, r.ModelsPath, ct);
        await audit.WriteAsync("provider.create", "provider", provider.PublicId.ToString(), new { provider.Name, provider.BaseUrl, template = r.TemplateCode });
        return Results.Created($"/admin/api/providers/{provider.PublicId}", await Query(db, provider.PublicId).FirstAsync(ct));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateProviderRequest r, GatewayDbContext db, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var internalId = await ids.ProviderAsync(id, ct);
        await prov.UpdateProviderAsync(internalId,
            new ProviderPatch(r.Name, r.BaseUrl, r.Enabled, r.AuthHeader, r.AuthPrefix, r.ModelsPath, r.ClearModelsPath == true), ct);
        await audit.WriteAsync("provider.update", "provider", id.ToString(), new { fields = Changed(r) });
        return Results.Ok(await Query(db, id).FirstAsync(ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.DeleteProviderAsync(await ids.ProviderAsync(id, ct), ct);
        await audit.WriteAsync("provider.delete", "provider", id.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> RotateAsync(
        Guid id, RotateKeyRequest r, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.RotateProviderKeyAsync(await ids.ProviderAsync(id, ct), r.ApiKey ?? "", ct);
        await audit.WriteAsync("provider.rotate_key", "provider", id.ToString()); // key tidak dicatat
        return Results.NoContent();
    }

    private static async Task<IResult> DiscoverAsync(Guid id, PublicIdResolver ids, ProvisioningService prov, CancellationToken ct) =>
        Results.Ok(new { models = await prov.DiscoverModelsAsync(await ids.ProviderAsync(id, ct), ct) });

    private static async Task<IResult> ImportAsync(
        Guid id, ImportModelsRequest r, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var providerId = await ids.ProviderAsync(id, ct);
        if (r.UpstreamModels is { Length: > MaxImportedModels })
            throw GatewayException.BadRequest("too_many_models", $"Maksimal {MaxImportedModels} model per impor.");
        var upstreamModels = r.UpstreamModels?.Distinct().ToArray();
        List<ModelSpec> specs;
        switch (r.Source)
        {
            case "catalog":
                specs = await prov.CatalogSpecsAsync(providerId, upstreamModels, ct);
                break;
            case "discovered":
                if (upstreamModels is not { Length: > 0 })
                    throw GatewayException.BadRequest("no_models", "upstreamModels wajib diisi untuk source=discovered.");
                specs = upstreamModels.Select(m => new ModelSpec(m, m)).ToList();
                break;
            default:
                throw GatewayException.BadRequest("invalid_source", "source harus catalog atau discovered.");
        }

        var created = await prov.ImportModelsAsync(providerId, specs, ct);
        await audit.WriteAsync("provider.import_models", "provider", id.ToString(), new { r.Source, created = created.Count, requested = specs.Count });
        return Results.Ok(new ImportModelsResponse(created.Select(m => new ImportedModel(m.PublicId, m.Alias)).ToList(), specs.Count - created.Count));
    }

    private static string[] Changed(UpdateProviderRequest r) => new (string, bool)[]
    {
        ("name", r.Name is not null), ("baseUrl", r.BaseUrl is not null), ("enabled", r.Enabled is not null),
        ("authHeader", r.AuthHeader is not null), ("authPrefix", r.AuthPrefix is not null), ("modelsPath", r.ModelsPath is not null || r.ClearModelsPath == true),
    }.Where(f => f.Item2).Select(f => f.Item1).ToArray();
}
