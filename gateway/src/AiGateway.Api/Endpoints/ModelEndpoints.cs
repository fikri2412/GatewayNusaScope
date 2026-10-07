using AiGateway.Api.Auth;
using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Endpoints;

public sealed record RouteDto(Guid ProviderId, string ProviderName, string UpstreamModel, int Priority, int Weight, bool Enabled);

public sealed record TierDto(int MinInputTokens, decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite);

public sealed record PriceDto(
    decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite, string Currency, DateTime EffectiveFrom, IReadOnlyList<TierDto> Tiers);

public sealed record ModelDto(
    Guid Id, string Alias, string? Description, int? MaxOutputTokens, bool Enabled,
    IReadOnlyList<RouteDto> Routes, PriceDto? Price, DateTime CreatedAt);

public sealed record RouteInput(Guid ProviderId, string? UpstreamModel, int? Priority, int? Weight);

public sealed record PriceInput(
    decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite, string? Currency, TierDto[]? Tiers);

public sealed record CreateModelRequest(
    string? Alias, string? Description, int? MaxOutputTokens, RouteInput[]? Routes, PriceInput? Price);

public sealed record UpdateModelRequest(string? Description, bool? Enabled, int? MaxOutputTokens, bool? ClearMaxOutputTokens);

public sealed record ReplaceRoutesRequest(RouteInput[]? Routes);

/// <summary>Model = nama yang dilihat klien (alias) → satu atau lebih route ke provider tenant, plus riwayat harga.</summary>
public static class ModelEndpoints
{
    public static IEndpointRouteBuilder MapAdminModels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/api/models");
        g.MapGet("", ListAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapGet("{id:guid}", GetAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPost("", CreateAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPatch("{id:guid}", UpdateAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapDelete("{id:guid}", DeleteAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPut("{id:guid}/routes", ReplaceRoutesAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/prices", AddPriceAsync).RequireAuthorization(Policies.TenantAdmin);
        return app;
    }

    private static async Task<List<ModelDto>> LoadAsync(GatewayDbContext db, Guid? publicId, CancellationToken ct)
    {
        var models = await db.Models.AsNoTracking().Where(m => publicId == null || m.PublicId == publicId)
            .OrderBy(m => m.Alias).Take(500).ToListAsync(ct);
        var modelIds = models.Select(m => m.Id).ToList();
        var routes = await (from r in db.ModelRoutes.AsNoTracking()
                            join p in db.Providers on r.ProviderId equals p.Id
                            where modelIds.Contains(r.ModelId)
                            orderby r.Priority, r.Id
                            select new { r.ModelId, ProviderPublicId = p.PublicId, ProviderName = p.Name, r.UpstreamModel, r.Priority, r.Weight, r.Enabled })
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var prices = await db.ModelPrices.AsNoTracking().Where(p => modelIds.Contains(p.ModelId) && p.EffectiveFrom <= now).ToListAsync(ct);

        return models.Select(m =>
        {
            var current = prices.Where(p => p.ModelId == m.Id).GroupBy(p => p.EffectiveFrom).OrderByDescending(g => g.Key).FirstOrDefault()?.ToList();
            var basePrice = current?.OrderBy(p => p.MinInputTokens).FirstOrDefault();
            var price = basePrice is null ? null : new PriceDto(
                basePrice.InputPricePer1M, basePrice.OutputPricePer1M, basePrice.CacheReadPricePer1M, basePrice.CacheWritePricePer1M,
                basePrice.Currency, basePrice.EffectiveFrom,
                current!.Where(p => p.MinInputTokens > 0).OrderBy(p => p.MinInputTokens)
                    .Select(p => new TierDto(p.MinInputTokens, p.InputPricePer1M, p.OutputPricePer1M, p.CacheReadPricePer1M, p.CacheWritePricePer1M)).ToList());
            return new ModelDto(
                m.PublicId, m.Alias, m.Description, m.MaxOutputTokens, m.Enabled,
                routes.Where(r => r.ModelId == m.Id).Select(r => new RouteDto(r.ProviderPublicId, r.ProviderName, r.UpstreamModel, r.Priority, r.Weight, r.Enabled)).ToList(),
                price, m.CreatedAt);
        }).ToList();
    }

    private static async Task<IResult> ListAsync(GatewayDbContext db, CancellationToken ct) => Results.Ok(await LoadAsync(db, null, ct));

    private static async Task<IResult> GetAsync(Guid id, GatewayDbContext db, CancellationToken ct) =>
        (await LoadAsync(db, id, ct)).FirstOrDefault() is { } dto ? Results.Ok(dto) : throw GatewayException.NotFound("Model");

    private static async Task<List<RouteSpec>> ToRoutesAsync(RouteInput[]? inputs, PublicIdResolver ids, CancellationToken ct)
    {
        var routes = new List<RouteSpec>();
        foreach (var r in inputs ?? [])
            routes.Add(new RouteSpec(await ids.ProviderAsync(r.ProviderId, ct), r.UpstreamModel ?? "", r.Priority ?? 0, r.Weight ?? 1));
        return routes;
    }

    private static async Task<IResult> CreateAsync(
        CreateModelRequest r, GatewayDbContext db, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var routes = await ToRoutesAsync(r.Routes, ids, ct);
        var spec = new ModelSpec(r.Alias ?? "", routes.FirstOrDefault()?.UpstreamModel ?? "", r.MaxOutputTokens,
            r.Price?.Input, r.Price?.Output, r.Price?.Currency ?? "USD", r.Price?.CacheRead, r.Price?.CacheWrite,
            r.Price?.Tiers?.Select(t => new PriceTier(t.MinInputTokens, t.Input, t.Output, t.CacheRead, t.CacheWrite)).ToList());
        var model = await prov.CreateModelAsync(spec, routes, ct);
        if (!string.IsNullOrEmpty(r.Description)) await prov.UpdateModelAsync(model.Id, new ModelPatch(Description: r.Description), ct);
        await audit.WriteAsync("model.create", "model", model.PublicId.ToString(), new { model.Alias, routes = routes.Count });
        return Results.Created($"/admin/api/models/{model.PublicId}", (await LoadAsync(db, model.PublicId, ct)).First());
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateModelRequest r, GatewayDbContext db, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.UpdateModelAsync(await ids.ModelAsync(id, ct),
            new ModelPatch(r.Description, r.Enabled, r.MaxOutputTokens, r.ClearMaxOutputTokens == true), ct);
        await audit.WriteAsync("model.update", "model", id.ToString());
        return Results.Ok((await LoadAsync(db, id, ct)).First());
    }

    private static async Task<IResult> DeleteAsync(Guid id, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.DeleteModelAsync(await ids.ModelAsync(id, ct), ct);
        await audit.WriteAsync("model.delete", "model", id.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> ReplaceRoutesAsync(
        Guid id, ReplaceRoutesRequest r, GatewayDbContext db, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var modelId = await ids.ModelAsync(id, ct);
        var routes = await ToRoutesAsync(r.Routes, ids, ct);
        await prov.ReplaceRoutesAsync(modelId, routes, ct);
        await audit.WriteAsync("model.routes", "model", id.ToString(), new { routes = routes.Count });
        return Results.Ok((await LoadAsync(db, id, ct)).First());
    }

    private static async Task<IResult> AddPriceAsync(
        Guid id, PriceInput r, GatewayDbContext db, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.AddPriceSetAsync(await ids.ModelAsync(id, ct),
            new PriceSet(r.Input, r.Output, r.CacheRead, r.CacheWrite, r.Currency ?? "USD",
                r.Tiers?.Select(t => new PriceTier(t.MinInputTokens, t.Input, t.Output, t.CacheRead, t.CacheWrite)).ToList()), ct);
        await audit.WriteAsync("model.price", "model", id.ToString(), new { r.Input, r.Output, tiers = r.Tiers?.Length ?? 0 });
        return Results.Ok((await LoadAsync(db, id, ct)).First());
    }
}
