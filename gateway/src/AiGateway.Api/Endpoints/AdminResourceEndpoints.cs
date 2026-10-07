using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AiGateway.Api.Auth;
using AiGateway.Core.Audit;
using AiGateway.Core.Auth;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Reports;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Endpoints;

public sealed record ProjectDto(Guid Id, string Name, string Status, bool LogContent, int? ContentRetentionDays, DateTime CreatedAt);
public sealed record CreateProjectRequest(string? Name = null, bool? LogContent = null, int? ContentRetentionDays = null);
public sealed record UpdateProjectRequest(
    string? Name = null, string? Status = null, bool? LogContent = null, int? ContentRetentionDays = null, bool? ClearRetention = null);

public sealed record ApiKeyDto(
    Guid Id, Guid ProjectId, string Name, string KeyPrefix, IReadOnlyList<string> AllowedIps,
    DateTime? ExpiresAt, DateTime? RevokedAt, DateTime? LastUsedAt, DateTime CreatedAt);

/// <param name="PlaintextKey">Hanya ada di jawaban pembuatan; tidak pernah disimpan atau dikembalikan lagi.</param>
public sealed record CreatedApiKeyDto(
    Guid Id, Guid ProjectId, string Name, string KeyPrefix, IReadOnlyList<string> AllowedIps,
    DateTime? ExpiresAt, DateTime? RevokedAt, DateTime? LastUsedAt, DateTime CreatedAt, string PlaintextKey);

public sealed record CreateApiKeyRequest(Guid ProjectId = default, string? Name = null, DateTime? ExpiresAt = null, string[]? AllowedIps = null);
public sealed record UpdateApiKeyRequest(string? Name = null, DateTime? ExpiresAt = null, bool? ClearExpiry = null, string[]? AllowedIps = null);

/// <param name="ScopeId">GUID publik target kebijakan: tenant (dari token), project, atau API key.</param>
public sealed record PolicyDto(
    string Scope, Guid ScopeId, int? MaxTokensPerRequest, int? RequestsPerMinute, long? DailyTokenQuota,
    long? MonthlyTokenQuota, decimal? MonthlyBudget, IReadOnlyList<string>? AllowedModels, bool Enabled);

public sealed record PolicyRequest(
    int? MaxTokensPerRequest = null, int? RequestsPerMinute = null, long? DailyTokenQuota = null,
    long? MonthlyTokenQuota = null, decimal? MonthlyBudget = null, string[]? AllowedModels = null, bool? Enabled = null);

/// <summary>Baris log tanpa id internal: project/key/model dikembalikan sebagai GUID publik (NULL bila tidak resolvable).</summary>
public sealed record UsageLogItem(
    long Id, DateTime CreatedAt, Guid RequestId, Guid? ProjectId, Guid? KeyId, Guid? ModelId, string Status, int HttpStatus,
    string? DeniedReason, int InputTokens, int OutputTokens, int CachedTokens, int ReasoningTokens, decimal Cost, int LatencyMs,
    int Attempts, bool FallbackUsed, string? EndUser);

public sealed record UsageLogPage(IReadOnlyList<UsageLogItem> Items, int Total, int PageNumber, int PageSize);

public sealed record UserDto(long Id, string Email, string DisplayName, string Role, bool IsActive, DateTime? LastLoginAt, DateTime CreatedAt);
public sealed record InviteUserRequest(string? Email = null, string? DisplayName = null, string? Role = null);
public sealed record UpdateUserRequest(string? Role = null, bool? IsActive = null);
public sealed record ResetTokenResponse(string Token);

public sealed record AuditItem(long Id, long? UserId, string Action, string? Entity, string? EntityId, string? DetailJson, string? Ip, DateTime CreatedAt);
public sealed record AuditPage(IReadOnlyList<AuditItem> Items, int Total, int PageNumber, int PageSize);

/// <summary>
/// Resource management tenant: project, API key, kebijakan, laporan pemakaian, user, dan audit.
/// Semua id resource berupa GUID publik; query filter tenant + resolver menjaga isolasi (target lintas tenant = 404).
/// </summary>
public static class AdminResourceEndpoints
{
    public static IEndpointRouteBuilder MapAdminResources(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/api");

        g.MapGet("projects", ListProjectsAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPost("projects", CreateProjectAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapGet("projects/{id:guid}", GetProjectAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPatch("projects/{id:guid}", UpdateProjectAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapDelete("projects/{id:guid}", DeleteProjectAsync).RequireAuthorization(Policies.TenantAdmin);

        g.MapGet("keys", ListKeysAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPost("keys", CreateKeyAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapGet("keys/{id:guid}", GetKeyAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPatch("keys/{id:guid}", UpdateKeyAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("keys/{id:guid}/revoke", RevokeKeyAsync).RequireAuthorization(Policies.TenantAdmin);

        g.MapGet("policies", ListPoliciesAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapGet("policies/{scope}/{scopeId:guid}", GetPolicyAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapPut("policies/{scope}/{scopeId:guid}", SetPolicyAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapDelete("policies/{scope}/{scopeId:guid}", DeletePolicyAsync).RequireAuthorization(Policies.TenantAdmin);

        g.MapGet("usage/summary", UsageSummaryAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapGet("usage/logs", UsageLogsAsync).RequireAuthorization(Policies.TenantViewer);
        g.MapGet("usage/export", UsageExportAsync).RequireAuthorization(Policies.TenantViewer);

        g.MapGet("users", ListUsersAsync).RequireAuthorization(Policies.TenantOwner);
        g.MapPost("users/invite", InviteUserAsync).RequireAuthorization(Policies.TenantOwner);
        g.MapPatch("users/{id:long}", UpdateUserAsync).RequireAuthorization(Policies.TenantOwner);
        g.MapPost("users/{id:long}/reset-password", ResetUserPasswordAsync).RequireAuthorization(Policies.TenantOwner);

        g.MapGet("audit", ListAuditAsync).RequireAuthorization(Policies.TenantViewer);
        return app;
    }

    // --- project -------------------------------------------------------------------------------

    private static IQueryable<ProjectDto> ProjectQuery(GatewayDbContext db) =>
        db.Projects.AsNoTracking().Select(p => new ProjectDto(p.PublicId, p.Name, p.Status, p.LogContent, p.ContentRetentionDays, p.CreatedAt));

    private static async Task<IResult> ListProjectsAsync(GatewayDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Projects.AsNoTracking().OrderBy(p => p.Name).Take(500)
            .Select(p => new ProjectDto(p.PublicId, p.Name, p.Status, p.LogContent, p.ContentRetentionDays, p.CreatedAt))
            .ToListAsync(ct));

    private static async Task<IResult> GetProjectAsync(Guid id, GatewayDbContext db, CancellationToken ct) =>
        await db.Projects.AsNoTracking().Where(p => p.PublicId == id)
            .Select(p => new ProjectDto(p.PublicId, p.Name, p.Status, p.LogContent, p.ContentRetentionDays, p.CreatedAt))
            .FirstOrDefaultAsync(ct) is { } dto ? Results.Ok(dto) : throw GatewayException.NotFound("Project");

    private static async Task<IResult> CreateProjectAsync(
        CreateProjectRequest r, GatewayDbContext db, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var project = await prov.CreateProjectAsync(r.Name ?? "", ct);
        if (r.LogContent is not null || r.ContentRetentionDays is not null)
            await prov.UpdateProjectAsync(project.Id, new ProjectPatch(LogContent: r.LogContent, ContentRetentionDays: r.ContentRetentionDays), ct);
        await audit.WriteAsync("project.create", "project", project.PublicId.ToString(), new { project.Name });
        return Results.Created($"/admin/api/projects/{project.PublicId}", await db.Projects.AsNoTracking().Where(p => p.PublicId == project.PublicId)
            .Select(p => new ProjectDto(p.PublicId, p.Name, p.Status, p.LogContent, p.ContentRetentionDays, p.CreatedAt))
            .FirstAsync(ct));
    }

    private static async Task<IResult> UpdateProjectAsync(
        Guid id, UpdateProjectRequest r, GatewayDbContext db, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.UpdateProjectAsync(await ids.ProjectAsync(id, ct),
            new ProjectPatch(r.Name, r.Status, r.LogContent, r.ContentRetentionDays, r.ClearRetention == true), ct);
        await audit.WriteAsync("project.update", "project", id.ToString(),
            new { fields = Fields(("name", r.Name), ("status", r.Status), ("logContent", r.LogContent),
                ("contentRetentionDays", r.ContentRetentionDays), ("clearRetention", r.ClearRetention == true ? true : null)) });
        return Results.Ok(await db.Projects.AsNoTracking().Where(p => p.PublicId == id)
            .Select(p => new ProjectDto(p.PublicId, p.Name, p.Status, p.LogContent, p.ContentRetentionDays, p.CreatedAt))
            .FirstAsync(ct));
    }

    private static async Task<IResult> DeleteProjectAsync(
        Guid id, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.DeleteProjectAsync(await ids.ProjectAsync(id, ct), ct);
        await audit.WriteAsync("project.delete", "project", id.ToString());
        return Results.NoContent();
    }

    // --- API key -------------------------------------------------------------------------------

    private static ApiKeyDto ToDto(ApiKey k, Guid projectPublicId) => new(
        k.PublicId, projectPublicId, k.Name, k.KeyPrefix, ParseIps(k.AllowedIpsJson),
        k.ExpiresAt, k.RevokedAt, k.LastUsedAt, k.CreatedAt);

    private static string[] ParseIps(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static async Task<List<ApiKeyDto>> LoadKeysAsync(GatewayDbContext db, IQueryable<ApiKey> keys, CancellationToken ct)
    {
        var list = await keys.AsNoTracking().OrderBy(k => k.Name).Take(500).ToListAsync(ct);
        var tenantId = db.CurrentTenantId;
        var projectIds = list.Select(k => k.ProjectId).Distinct().ToList();
        var projects = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.PublicId, ct);
        return list.Select(k => ToDto(k, projects.GetValueOrDefault(k.ProjectId))).ToList();
    }

    private static async Task<IResult> ListKeysAsync(Guid? projectId, GatewayDbContext db, PublicIdResolver ids, CancellationToken ct)
    {
        IQueryable<ApiKey> keys = db.ApiKeys;
        if (projectId is { } pid)
        {
            var projectInternalId = await ids.ProjectAsync(pid, ct);
            keys = keys.Where(k => k.ProjectId == projectInternalId);
        }
        return Results.Ok(await LoadKeysAsync(db, keys, ct));
    }

    private static async Task<IResult> GetKeyAsync(Guid id, GatewayDbContext db, CancellationToken ct) =>
        (await LoadKeysAsync(db, db.ApiKeys.Where(k => k.PublicId == id), ct)).FirstOrDefault() is { } dto
            ? Results.Ok(dto) : throw GatewayException.NotFound("API key");

    private static async Task<IResult> CreateKeyAsync(
        CreateApiKeyRequest r, ClaimsPrincipal principal, GatewayDbContext db, PublicIdResolver ids,
        ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var projectInternalId = await ids.ProjectAsync(r.ProjectId, ct);
        var created = await prov.CreateApiKeyAsync(projectInternalId, r.Name ?? "", r.ExpiresAt, r.AllowedIps, UserId(principal), ct);
        await audit.WriteAsync("key.create", "key", created.Entity.PublicId.ToString(), new { created.Entity.Name, projectId = r.ProjectId });
        var dto = ToDto(created.Entity, r.ProjectId);
        return Results.Created($"/admin/api/keys/{dto.Id}",
            new CreatedApiKeyDto(dto.Id, dto.ProjectId, dto.Name, dto.KeyPrefix, dto.AllowedIps, dto.ExpiresAt, dto.RevokedAt, dto.LastUsedAt, dto.CreatedAt, created.PlaintextKey));
    }

    private static async Task<IResult> UpdateKeyAsync(
        Guid id, UpdateApiKeyRequest r, GatewayDbContext db, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.UpdateApiKeyAsync(await ids.ApiKeyAsync(id, ct),
            new ApiKeyPatch(r.Name, r.AllowedIps, r.ExpiresAt, r.ClearExpiry == true), ct);
        await audit.WriteAsync("key.update", "key", id.ToString(),
            new { fields = Fields(("name", r.Name), ("expiresAt", r.ExpiresAt), ("clearExpiry", r.ClearExpiry == true ? true : null), ("allowedIps", r.AllowedIps)) });
        return Results.Ok((await LoadKeysAsync(db, db.ApiKeys.Where(k => k.PublicId == id), ct)).First());
    }

    private static async Task<IResult> RevokeKeyAsync(
        Guid id, PublicIdResolver ids, ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.RevokeApiKeyAsync(await ids.ApiKeyAsync(id, ct), ct);
        await audit.WriteAsync("key.revoke", "key", id.ToString());
        return Results.NoContent();
    }

    // --- kebijakan -----------------------------------------------------------------------------

    private static string[]? ParseModels(string? json) => json is null ? null : JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static PolicyDto ToDto(Policy p, Guid scopeId) => new(
        p.Scope, scopeId, p.MaxTokensPerRequest, p.RequestsPerMinute, p.DailyTokenQuota, p.MonthlyTokenQuota,
        p.MonthlyBudget, ParseModels(p.AllowedModelsJson), p.Enabled);

    /// <summary>GUID publik → id internal dalam tenant konteks; scope tidak valid = 400, target asing = 404.</summary>
    private static async Task<long> ResolvePolicyTargetAsync(
        string scope, Guid scopeId, GatewayDbContext db, PublicIdResolver ids, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId;
        return scope switch
        {
            PolicyScopes.Tenant => await db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId && t.PublicId == scopeId).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct)
                ?? throw GatewayException.NotFound("Kebijakan"),
            PolicyScopes.Project => await ids.ProjectAsync(scopeId, ct),
            PolicyScopes.Key => await ids.ApiKeyAsync(scopeId, ct),
            _ => throw GatewayException.BadRequest("invalid_scope", "scope harus tenant, project, atau key."),
        };
    }

    private static async Task<List<PolicyDto>> LoadPoliciesAsync(GatewayDbContext db, IQueryable<Policy> query, CancellationToken ct)
    {
        var policies = await query.AsNoTracking().OrderBy(p => p.Scope).ThenBy(p => p.ScopeId).Take(1000).ToListAsync(ct);
        var tenantId = db.CurrentTenantId;
        var projectIds = policies.Where(p => p.Scope == PolicyScopes.Project).Select(p => p.ScopeId).Distinct().ToList();
        var keyIds = policies.Where(p => p.Scope == PolicyScopes.Key).Select(p => p.ScopeId).Distinct().ToList();
        var projects = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PublicId, ct);
        var keys = await db.ApiKeys.AsNoTracking().Where(k => keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, k => k.PublicId, ct);
        var tenantPublicId = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.PublicId).FirstAsync(ct);
        return policies.Select(p => new PolicyDto(p.Scope,
            p.Scope == PolicyScopes.Tenant ? tenantPublicId
                : p.Scope == PolicyScopes.Project ? projects.GetValueOrDefault(p.ScopeId)
                : keys.GetValueOrDefault(p.ScopeId),
            p.MaxTokensPerRequest, p.RequestsPerMinute, p.DailyTokenQuota, p.MonthlyTokenQuota,
            p.MonthlyBudget, ParseModels(p.AllowedModelsJson), p.Enabled)).ToList();
    }

    private static async Task<IResult> ListPoliciesAsync(GatewayDbContext db, CancellationToken ct) =>
        Results.Ok(await LoadPoliciesAsync(db, db.Policies, ct));

    private static async Task<IResult> GetPolicyAsync(
        string scope, Guid scopeId, GatewayDbContext db, PublicIdResolver ids, CancellationToken ct)
    {
        var target = await ResolvePolicyTargetAsync(scope, scopeId, db, ids, ct);
        return Results.Ok((await LoadPoliciesAsync(db, db.Policies.Where(p => p.Scope == scope && p.ScopeId == target), ct)).FirstOrDefault()
            ?? throw GatewayException.NotFound("Kebijakan"));
    }

    private static async Task<IResult> SetPolicyAsync(
        string scope, Guid scopeId, PolicyRequest r, GatewayDbContext db, PublicIdResolver ids,
        ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        var target = await ResolvePolicyTargetAsync(scope, scopeId, db, ids, ct);
        var policy = await prov.SetPolicyAsync(scope, target,
            new PolicySpec(r.MaxTokensPerRequest, r.RequestsPerMinute, r.DailyTokenQuota, r.MonthlyTokenQuota, r.MonthlyBudget, r.AllowedModels, r.Enabled ?? true), ct);
        await audit.WriteAsync("policy.set", "policy", $"{scope}/{scopeId}", new { scope, scopeId });
        return Results.Ok(ToDto(policy, scopeId));
    }

    private static async Task<IResult> DeletePolicyAsync(
        string scope, Guid scopeId, GatewayDbContext db, PublicIdResolver ids,
        ProvisioningService prov, AuditWriter audit, CancellationToken ct)
    {
        await prov.DeletePolicyAsync(scope, await ResolvePolicyTargetAsync(scope, scopeId, db, ids, ct), ct);
        await audit.WriteAsync("policy.delete", "policy", $"{scope}/{scopeId}");
        return Results.NoContent();
    }

    // --- pemakaian -----------------------------------------------------------------------------

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryTime(string? value, out DateTime time) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out time);

    private static async Task<IResult> UsageSummaryAsync(
        string? from, string? to, string? groupBy, GatewayDbContext db, UsageReports reports, CancellationToken ct)
    {
        if (!TryDate(from, out var f) || !TryDate(to, out var t))
            throw GatewayException.BadRequest("invalid_range", "from dan to wajib tanggal YYYY-MM-DD.");
        var group = string.IsNullOrEmpty(groupBy) ? "day" : groupBy;
        return Results.Ok(await RemapSummaryAsync(await reports.SummaryAsync(f, t, group, ct), group, db, ct));
    }

    /// <summary>Key grup berupa id internal di service; diganti GUID publik agar tidak bocor ke klien.</summary>
    private static async Task<List<UsageRow>> RemapSummaryAsync(List<UsageRow> rows, string groupBy, GatewayDbContext db, CancellationToken ct)
    {
        if (groupBy == "day" || rows.Count == 0) return rows;
        var internalIds = rows.Select(r => long.TryParse(r.Key, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0).Distinct().ToList();
        var tenantId = db.CurrentTenantId;
        var map = groupBy switch
        {
            "project" => await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.TenantId == tenantId && internalIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PublicId, ct),
            "model" => await db.Models.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == tenantId && internalIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.PublicId, ct),
            _ => await db.ApiKeys.AsNoTracking().Where(k => internalIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, k => k.PublicId, ct),
        };
        return rows.Select(r => long.TryParse(r.Key, CultureInfo.InvariantCulture, out var id) && id > 0
            ? r with { Key = map.TryGetValue(id, out var pub) ? pub.ToString() : "0" }
            : r).ToList();
    }

    private static async Task<UsageFilter> BuildUsageFilterAsync(
        string? from, string? to, Guid? projectId, Guid? modelId, Guid? keyId, string? status,
        PublicIdResolver ids, CancellationToken ct)
    {
        if (!TryTime(from, out var f) || !TryTime(to, out var t))
            throw GatewayException.BadRequest("invalid_range", "from dan to wajib waktu ISO (mis. 2026-01-01T00:00:00Z).");
        if (status is not null && status is not (UsageStatuses.Ok or UsageStatuses.Error or UsageStatuses.Denied))
            throw GatewayException.BadRequest("invalid_status", "status harus ok, error, atau denied.");
        return new UsageFilter(f, t,
            projectId is { } p ? await ids.ProjectAsync(p, ct) : null,
            keyId is { } k ? await ids.ApiKeyAsync(k, ct) : null,
            modelId is { } m ? await ids.ModelAsync(m, ct) : null,
            status);
    }

    /// <summary>Project/key/model di baris log hanya boleh tampil sebagai GUID publik.</summary>
    private static async Task<UsageLogPage> RemapLogsAsync(Page<UsageLogRow> page, GatewayDbContext db, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId;
        var projectIds = page.Items.Select(i => i.ProjectId).Distinct().ToList();
        var keyIds = page.Items.Select(i => i.ApiKeyId).Distinct().ToList();
        var modelIds = page.Items.Where(i => i.ModelId > 0).Select(i => i.ModelId!.Value).Distinct().ToList();
        var projects = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PublicId, ct);
        var keys = await db.ApiKeys.AsNoTracking().Where(k => keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, k => k.PublicId, ct);
        var models = await db.Models.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && modelIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.PublicId, ct);
        var items = page.Items.Select(i => new UsageLogItem(
            i.Id, i.CreatedAt, i.RequestId,
            projects.TryGetValue(i.ProjectId, out var p) ? p : null,
            keys.TryGetValue(i.ApiKeyId, out var k) ? k : null,
            i.ModelId is { } m && models.TryGetValue(m, out var model) ? model : null,
            i.Status, i.HttpStatus, i.DeniedReason, i.InputTokens, i.OutputTokens, i.CachedTokens, i.ReasoningTokens,
            i.Cost, i.LatencyMs, i.Attempts, i.FallbackUsed, i.EndUser)).ToList();
        return new UsageLogPage(items, page.Total, page.PageNumber, page.PageSize);
    }

    private static async Task<IResult> UsageLogsAsync(
        string? from, string? to, Guid? projectId, Guid? modelId, Guid? keyId, string? status, int? page, int? pageSize,
        GatewayDbContext db, PublicIdResolver ids, UsageReports reports, CancellationToken ct)
    {
        var filter = await BuildUsageFilterAsync(from, to, projectId, modelId, keyId, status, ids, ct);
        var result = await reports.LogsAsync(filter, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(await RemapLogsAsync(result, db, ct));
    }

    /// <summary>Ekspor CSV dengan id publik; pagar baris sama dengan basis data (MaxExportRows).</summary>
    private static async Task UsageExportAsync(
        string? from, string? to, Guid? projectId, Guid? modelId, Guid? keyId, string? status,
        HttpContext http, GatewayDbContext db, PublicIdResolver ids, UsageReports reports, CancellationToken ct)
    {
        var filter = await BuildUsageFilterAsync(from, to, projectId, modelId, keyId, status, ids, ct);
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers["Content-Disposition"] = "attachment; filename=\"usage.csv\"";
        await using var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync("id,created_at,request_id,project_id,api_key_id,model_id,status,http_status,denied_reason,input_tokens,output_tokens,cached_tokens,reasoning_tokens,cost,latency_ms,attempts,fallback_used,end_user,tags");
        var pageNumber = 1;
        var written = 0;
        while (written < UsageReports.MaxExportRows)
        {
            var page = await reports.LogsAsync(filter, pageNumber, UsageReports.MaxPageSize, ct);
            if (page.Items.Count == 0) break;
            var remapped = await RemapLogsAsync(page, db, ct);
            for (var i = 0; i < remapped.Items.Count && written < UsageReports.MaxExportRows; i++, written++)
            {
                var l = remapped.Items[i];
                await writer.WriteLineAsync(string.Join(',',
                    l.Id, l.CreatedAt.ToString("O", CultureInfo.InvariantCulture), l.RequestId, l.ProjectId, l.KeyId, l.ModelId,
                    UsageReports.Csv(l.Status), l.HttpStatus, UsageReports.Csv(l.DeniedReason), l.InputTokens, l.OutputTokens,
                    l.CachedTokens, l.ReasoningTokens, l.Cost.ToString(CultureInfo.InvariantCulture), l.LatencyMs, l.Attempts,
                    l.FallbackUsed, UsageReports.Csv(l.EndUser), UsageReports.Csv(page.Items[i].Tags)));
            }
            if ((long)pageNumber * page.PageSize >= page.Total) break;
            pageNumber++;
        }
        await writer.FlushAsync(ct);
    }

    // --- user tenant ---------------------------------------------------------------------------

    private static UserDto ToDto(User u) => new(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.LastLoginAt, u.CreatedAt);

    private static async Task<IResult> ListUsersAsync(UserAdminService users, CancellationToken ct) =>
        Results.Ok((await users.ListAsync(ct)).Select(ToDto).ToList());

    private static async Task<IResult> InviteUserAsync(InviteUserRequest r, UserAdminService users, AuditWriter audit, CancellationToken ct)
    {
        var invited = await users.InviteAsync(r.Email ?? "", r.DisplayName ?? "", r.Role ?? "", ct);
        await audit.WriteAsync("user.invite", "user", invited.User.Id.ToString(), new { invited.User.Email, invited.User.Role });
        return Results.Ok(new { user = ToDto(invited.User), inviteToken = invited.InviteToken });
    }

    private static async Task<IResult> UpdateUserAsync(
        long id, UpdateUserRequest r, ClaimsPrincipal principal, UserAdminService users, AuditWriter audit, CancellationToken ct)
    {
        var user = await users.UpdateAsync(id, r.Role, r.IsActive, UserId(principal), ct);
        await audit.WriteAsync("user.update", "user", id.ToString(), new { r.Role, r.IsActive });
        return Results.Ok(ToDto(user));
    }

    private static async Task<IResult> ResetUserPasswordAsync(long id, UserAdminService users, AuditWriter audit, CancellationToken ct)
    {
        var token = await users.IssueResetTokenAsync(id, ct);
        await audit.WriteAsync("user.reset_password", "user", id.ToString()); // token tidak pernah dicatat
        return Results.Ok(new ResetTokenResponse(token));
    }

    // --- audit ---------------------------------------------------------------------------------

    private static async Task<IResult> ListAuditAsync(
        string? from, string? to, int? page, int? pageSize, GatewayDbContext db, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId; // filter eksplisit: audit_logs tidak punya query filter global
        var q = db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (from is not null)
        {
            if (!TryTime(from, out var f)) throw GatewayException.BadRequest("invalid_range", "from bukan waktu ISO yang valid.");
            q = q.Where(a => a.CreatedAt >= f);
        }
        if (to is not null)
        {
            if (!TryTime(to, out var t)) throw GatewayException.BadRequest("invalid_range", "to bukan waktu ISO yang valid.");
            q = q.Where(a => a.CreatedAt < t);
        }
        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        var number = Math.Max(page ?? 1, 1);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Id).Skip((number - 1) * size).Take(size)
            .Select(a => new AuditItem(a.Id, a.UserId, a.Action, a.Entity, a.EntityId, a.DetailJson, a.Ip, a.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(new AuditPage(items, total, number, size));
    }

    // --- bersama -------------------------------------------------------------------------------

    private static long UserId(ClaimsPrincipal principal) =>
        long.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

    /// <summary>Nama field yang benar-benar diisi, untuk detail audit (tanpa nilai rahasia).</summary>
    private static string[] Fields(params (string Name, object? Value)[] fields) =>
        fields.Where(f => f.Value is not null).Select(f => f.Name).ToArray();
}
