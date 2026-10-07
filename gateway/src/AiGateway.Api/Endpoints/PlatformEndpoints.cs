using System.Text.Json;
using AiGateway.Api.Auth;
using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Endpoints;

public sealed record PlanDto(long Id, string Name, int? MaxProjects, int? MaxApiKeys, long? MaxRequestsPerMonth, long? MaxTokensPerMonth);
/// <summary>Body tulis plan; <see cref="Id"/> diabaikan (dikirim balik dari DTO).</summary>
public sealed record PlanRequest(long? Id, string? Name, int? MaxProjects, int? MaxApiKeys, long? MaxRequestsPerMonth, long? MaxTokensPerMonth);

/// <summary><see cref="TenantDto.Id"/> adalah public_id (GUID) yang dipakai di URL.</summary>
public sealed record TenantDto(Guid Id, string Name, string Slug, string Status, long PlanId, string PlanName, DateTime CreatedAt);

public sealed record CreateTenantRequest(string? Name, string? Slug, long? PlanId, string? OwnerEmail, string? OwnerDisplayName);
public sealed record UpdateTenantRequest(string? Name, string? Status, long? PlanId);
public sealed record InviteOwnerRequest(string? Email, string? DisplayName);
public sealed record CreatedTenantResponse(TenantDto Tenant, string InviteToken);

/// <summary>User tenant tanpa hash/token; <c>role</c> owner/admin/viewer.</summary>
public sealed record PlatformUserDto(long Id, string Email, string DisplayName, string Role, bool IsActive, DateTime? LastLoginAt, DateTime CreatedAt);

public sealed record InvitedOwnerResponse(PlatformUserDto User, string InviteToken);
public sealed record CatalogSyncRequest(string? ServiceKey, string? Url);

public sealed record TemplateDto(
    long Id, string Code, string Name, string Type, string DefaultBaseUrl, string AuthHeader, string AuthPrefix,
    string? ModelsPath, string? SyncKind, string? SyncUrl, bool Enabled);

/// <summary>Body tulis template; <see cref="Id"/> diabaikan.</summary>
public sealed record TemplateRequest(
    long? Id, string? Code, string? Name, string? Type, string? DefaultBaseUrl, string? AuthHeader, string? AuthPrefix,
    string? ModelsPath, string? SyncKind, string? SyncUrl, bool? Enabled);

public sealed record CatalogModelDto(
    long Id, string TemplateCode, string UpstreamModel, string DisplayName, string ApiFamily, int? ContextWindow,
    int? MaxInputTokens, int? MaxOutputTokens, decimal? InputPricePer1M, decimal? OutputPricePer1M,
    decimal? CacheReadPricePer1M, decimal? CacheWritePricePer1M, IReadOnlyList<TierDto> Tiers, string Currency,
    bool SupportsTools, bool SupportsVision, bool SupportsReasoning, string Source, bool Enabled);

/// <summary>Body tulis model katalog; <see cref="Id"/> dan <see cref="Source"/> diabaikan (source selalu manual).</summary>
public sealed record CatalogModelRequest(
    long? Id, string? TemplateCode, string? UpstreamModel, string? DisplayName, string? ApiFamily, int? ContextWindow,
    int? MaxInputTokens, int? MaxOutputTokens, decimal? InputPricePer1M, decimal? OutputPricePer1M,
    decimal? CacheReadPricePer1M, decimal? CacheWritePricePer1M, TierDto[]? Tiers, string? Currency,
    bool? SupportsTools, bool? SupportsVision, bool? SupportsReasoning, string? Source, bool? Enabled);

/// <summary>
/// Platform plane (<c>/platform/api</c>, hanya <c>platform_admin</c>) dan master data katalog
/// (<c>/admin/api/catalog</c>, dibaca tenant mana pun maupun platform admin).
/// </summary>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformManagement(this IEndpointRouteBuilder app)
    {
        // Dibaca viewer tenant dan platform admin; hanya platform admin yang boleh menulis (grup di bawah).
        var catalogRead = app.MapGroup("/admin/api/catalog").RequireAuthorization(new AuthorizeAttribute
        {
            Roles = $"{Roles.PlatformAdmin},{Roles.Owner},{Roles.Admin},{Roles.Viewer}",
        });
        catalogRead.MapGet("templates", ListTemplatesAsync);
        catalogRead.MapGet("models", ListCatalogModelsAsync);

        var p = app.MapGroup("/platform/api").RequireAuthorization(Policies.PlatformAdmin);
        p.MapGet("plans", ListPlansAsync);
        p.MapPost("plans", CreatePlanAsync);
        p.MapPatch("plans/{id:long}", UpdatePlanAsync);
        p.MapDelete("plans/{id:long}", DeletePlanAsync);
        p.MapGet("tenants", ListTenantsAsync);
        p.MapPost("tenants", CreateTenantAsync);
        p.MapGet("tenants/{id:guid}", GetTenantAsync);
        p.MapPatch("tenants/{id:guid}", UpdateTenantAsync);
        p.MapPost("tenants/{id:guid}/invite-owner", InviteOwnerAsync);
        p.MapPost("tenants/{id:guid}/reset-password/{userId:long}", ResetPasswordAsync);
        p.MapGet("usage", PlatformUsageAsync);
        p.MapGet("audit", PlatformAuditAsync);
        p.MapPost("templates", CreateTemplateAsync);
        p.MapPut("templates/{id:long}", UpdateTemplateAsync);
        p.MapPost("catalog", UpsertCatalogAsync);
        p.MapPut("catalog/{id:long}", UpdateCatalogAsync);
        p.MapPost("catalog/sync", SyncCatalogAsync);
        return app;
    }

    // --- plan ----------------------------------------------------------------------------------

    private static async Task<IResult> ListPlansAsync(PlatformService platform, CancellationToken ct) =>
        Results.Ok((await platform.ListPlansAsync(ct)).Select(ToDto).ToList());

    private static async Task<IResult> CreatePlanAsync(PlanRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var plan = await platform.CreatePlanAsync(Spec(r), ct);
        await audit.WriteAsync("plan.create", "plan", plan.Id.ToString(), new { plan.Name });
        return Results.Created($"/platform/api/plans/{plan.Id}", ToDto(plan));
    }

    private static async Task<IResult> UpdatePlanAsync(long id, PlanRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var plan = await platform.UpdatePlanAsync(id, Spec(r), ct);
        await audit.WriteAsync("plan.update", "plan", plan.Id.ToString(), new { plan.Name });
        return Results.Ok(ToDto(plan));
    }

    private static async Task<IResult> DeletePlanAsync(long id, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        await platform.DeletePlanAsync(id, ct);
        await audit.WriteAsync("plan.delete", "plan", id.ToString());
        return Results.NoContent();
    }

    private static PlanSpec Spec(PlanRequest r) => new(r.Name, r.MaxProjects, r.MaxApiKeys, r.MaxRequestsPerMonth, r.MaxTokensPerMonth);

    private static PlanDto ToDto(Plan p) => new(p.Id, p.Name, p.MaxProjects, p.MaxApiKeys, p.MaxRequestsPerMonth, p.MaxTokensPerMonth);

    // --- tenant --------------------------------------------------------------------------------

    private static async Task<IResult> ListTenantsAsync(PlatformService platform, CancellationToken ct) =>
        Results.Ok((await platform.ListTenantsAsync(ct)).Select(ToDto).ToList());

    private static async Task<IResult> GetTenantAsync(Guid id, PlatformService platform, CancellationToken ct) =>
        Results.Ok(ToDto(await platform.GetTenantAsync(id, ct)));

    private static async Task<IResult> CreateTenantAsync(CreateTenantRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var (tenant, owner) = await platform.CreateTenantAsync(r.Name, r.Slug, r.PlanId, r.OwnerEmail, r.OwnerDisplayName, ct);
        await audit.WriteAsync("tenant.create", "tenant", tenant.PublicId.ToString(),
            new { tenant.Name, tenant.Slug, planId = tenant.PlanId, ownerEmail = owner.User.Email });
        var dto = ToDto(await platform.GetTenantAsync(tenant.PublicId, ct));
        return Results.Created($"/platform/api/tenants/{tenant.PublicId}", new CreatedTenantResponse(dto, owner.InviteToken));
    }

    private static async Task<IResult> UpdateTenantAsync(Guid id, UpdateTenantRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        await platform.UpdateTenantAsync(id, new TenantPatch(r.Name, r.Status, r.PlanId), ct);
        await audit.WriteAsync("tenant.update", "tenant", id.ToString(), new { fields = Changed(r) });
        return Results.Ok(ToDto(await platform.GetTenantAsync(id, ct)));
    }

    private static async Task<IResult> InviteOwnerAsync(Guid id, InviteOwnerRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var invited = await platform.InviteOwnerAsync(id, r.Email, r.DisplayName, ct);
        await audit.WriteAsync("tenant.invite_owner", "user", invited.User.Id.ToString(), new { tenant = id, email = invited.User.Email });
        return Results.Created($"/platform/api/tenants/{id}", new InvitedOwnerResponse(ToDto(invited.User), invited.InviteToken));
    }

    private static async Task<IResult> ResetPasswordAsync(Guid id, long userId, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var token = await platform.ResetUserPasswordAsync(id, userId, ct);
        await audit.WriteAsync("tenant.reset_password", "user", userId.ToString(), new { tenant = id });
        return Results.Ok(new { token });
    }

    private static string[] Changed(UpdateTenantRequest r) => new (string, bool)[]
    {
        ("name", r.Name is not null), ("status", r.Status is not null), ("planId", r.PlanId is not null),
    }.Where(f => f.Item2).Select(f => f.Item1).ToArray();

    // --- ringkasan lintas tenant ---------------------------------------------------------------

    private static async Task<IResult> PlatformUsageAsync(DateOnly from, DateOnly to, PlatformService platform, CancellationToken ct) =>
        Results.Ok(await platform.PlatformUsageAsync(from, to, ct));

    private static async Task<IResult> PlatformAuditAsync(DateTime? from, DateTime? to, int? page, int? pageSize, PlatformService platform, CancellationToken ct) =>
        Results.Ok(await platform.PlatformAuditAsync(from, to, page ?? 1, pageSize ?? 50, ct));

    // --- katalog master: baca ------------------------------------------------------------------

    private static async Task<IResult> ListTemplatesAsync(GatewayDbContext db, CancellationToken ct) =>
        Results.Ok(await db.ProviderTemplates.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new TemplateDto(t.Id, t.Code, t.Name, t.Type, t.DefaultBaseUrl, t.AuthHeader, t.AuthPrefix,
                t.ModelsPath, t.SyncKind, t.SyncUrl, t.Enabled)).Take(500).ToListAsync(ct));

    private static async Task<IResult> ListCatalogModelsAsync(string? templateCode, GatewayDbContext db, CancellationToken ct) =>
        Results.Ok(await LoadCatalogAsync(db, null, templateCode, ct));

    private static async Task<List<CatalogModelDto>> LoadCatalogAsync(GatewayDbContext db, long? id, string? templateCode, CancellationToken ct)
    {
        var rows = await (from m in db.CatalogModels.AsNoTracking()
                          join t in db.ProviderTemplates on m.TemplateId equals t.Id
                          where (id == null || m.Id == id) && (templateCode == null || t.Code == templateCode)
                          orderby t.Code, m.UpstreamModel
                          select new { Model = m, TemplateCode = t.Code }).Take(2000).ToListAsync(ct);
        return rows.Select(r => ToDto(r.Model, r.TemplateCode)).ToList();
    }

    private static async Task<CatalogModelDto> OneCatalogAsync(GatewayDbContext db, long id, CancellationToken ct) =>
        (await LoadCatalogAsync(db, id, null, ct)).FirstOrDefault() ?? throw GatewayException.NotFound("Model katalog");

    private static CatalogModelDto ToDto(CatalogModel m, string templateCode) => new(
        m.Id, templateCode, m.UpstreamModel, m.DisplayName, m.ApiFamily, m.ContextWindow, m.MaxInputTokens, m.MaxOutputTokens,
        m.InputPricePer1M, m.OutputPricePer1M, m.CacheReadPricePer1M, m.CacheWritePricePer1M, Tiers(m.ExtraTiersJson),
        m.Currency, m.SupportsTools, m.SupportsVision, m.SupportsReasoning, m.Source, m.Enabled);

    private static IReadOnlyList<TierDto> Tiers(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        var tiers = JsonSerializer.Deserialize<List<PriceTier>>(json) ?? [];
        return tiers.Select(t => new TierDto(t.MinInputTokens, t.Input, t.Output, t.CacheRead, t.CacheWrite)).ToList();
    }

    // --- katalog master: tulis -----------------------------------------------------------------

    private static async Task<IResult> CreateTemplateAsync(TemplateRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var template = await platform.CreateTemplateAsync(Spec(r), ct);
        await audit.WriteAsync("template.create", "template", template.Id.ToString(), new { template.Code, template.Name });
        return Results.Created($"/platform/api/templates/{template.Id}", ToDto(template));
    }

    private static async Task<IResult> UpdateTemplateAsync(long id, TemplateRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var template = await platform.UpdateTemplateAsync(id, Spec(r), ct);
        await audit.WriteAsync("template.update", "template", id.ToString(), new { template.Code, template.Enabled });
        return Results.Ok(ToDto(template));
    }

    private static async Task<IResult> UpsertCatalogAsync(CatalogModelRequest r, GatewayDbContext db, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var model = await platform.UpsertCatalogModelAsync(Spec(r), ct);
        await audit.WriteAsync("catalog.upsert", "catalog_model", model.Id.ToString(), new { templateId = model.TemplateId, model.UpstreamModel });
        return Results.Ok(await OneCatalogAsync(db, model.Id, ct));
    }

    private static async Task<IResult> UpdateCatalogAsync(long id, CatalogModelRequest r, GatewayDbContext db, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var model = await platform.UpdateCatalogModelAsync(id, Spec(r), ct);
        await audit.WriteAsync("catalog.update", "catalog_model", id.ToString(), new { templateId = model.TemplateId, model.UpstreamModel });
        return Results.Ok(await OneCatalogAsync(db, model.Id, ct));
    }

    private static async Task<IResult> SyncCatalogAsync(CatalogSyncRequest r, PlatformService platform, AuditWriter audit, CancellationToken ct)
    {
        var result = await platform.SyncCatalogAsync(r.ServiceKey, r.Url, ct);
        // serviceKey tidak pernah disimpan atau masuk ke detail audit.
        await audit.WriteAsync("catalog.sync", "catalog", detail: new
        {
            result.TemplatesAdded, result.ModelsAdded, result.ModelsUpdated, result.ModelsSkippedManual, result.ModelsUnsupported,
        });
        return Results.Ok(result);
    }

    private static TemplateDto ToDto(ProviderTemplate t) => new(
        t.Id, t.Code, t.Name, t.Type, t.DefaultBaseUrl, t.AuthHeader, t.AuthPrefix, t.ModelsPath, t.SyncKind, t.SyncUrl, t.Enabled);

    private static TemplateSpec Spec(TemplateRequest r) => new(
        r.Code, r.Name, r.Type, r.DefaultBaseUrl, r.AuthHeader, r.AuthPrefix, r.ModelsPath, r.SyncKind, r.SyncUrl, r.Enabled ?? true);

    private static CatalogModelSpec Spec(CatalogModelRequest r) => new(
        r.TemplateCode, r.UpstreamModel, r.DisplayName, r.ApiFamily, r.ContextWindow, r.MaxInputTokens, r.MaxOutputTokens,
        r.InputPricePer1M, r.OutputPricePer1M, r.CacheReadPricePer1M, r.CacheWritePricePer1M,
        r.Tiers?.Select(t => new PriceTier(t.MinInputTokens, t.Input, t.Output, t.CacheRead, t.CacheWrite)).ToList(),
        r.Currency, r.SupportsTools ?? false, r.SupportsVision ?? false, r.SupportsReasoning ?? false, r.Enabled ?? true);

    private static TenantDto ToDto(TenantRow row) => new(
        row.Tenant.PublicId, row.Tenant.Name, row.Tenant.Slug, row.Tenant.Status, row.Tenant.PlanId, row.PlanName, row.Tenant.CreatedAt);

    private static PlatformUserDto ToDto(User u) => new(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.LastLoginAt, u.CreatedAt);
}
