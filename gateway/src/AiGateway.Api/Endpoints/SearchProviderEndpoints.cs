using AiGateway.Api.Auth;
using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Security;
using AiGateway.Core.WebSearch;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiGateway.Api.Endpoints;

public sealed record SearchProviderDto(
    Guid Id,
    string Name,
    string Type,
    string BaseUri,
    string KeyHint,
    bool IsEnabled,
    int Weight,
    string Capabilities,
    decimal? MonthlyBudget,
    decimal CostPerSearch,
    decimal CostPerExtract,
    string ResetPolicy,
    string? ErrorMappingOverride,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CreateSearchProviderRequest(
    string? Type,
    string? Name,
    string? BaseUri,
    string? ApiKey,
    int? Weight,
    string? Capabilities,
    decimal? MonthlyBudget,
    decimal? CostPerSearch,
    decimal? CostPerExtract,
    string? ResetPolicy,
    string? ErrorMappingOverride);

public sealed record UpdateSearchProviderRequest(
    string? Name,
    string? BaseUri,
    string? ApiKey,
    bool? IsEnabled,
    int? Weight,
    string? Capabilities,
    decimal? MonthlyBudget,
    bool? ClearMonthlyBudget,
    decimal? CostPerSearch,
    decimal? CostPerExtract,
    string? ResetPolicy,
    string? ErrorMappingOverride,
    bool? ClearErrorMappingOverride);

public sealed record SearchProviderStatusDto(
    Guid Id,
    string Name,
    string Type,
    string Status,
    DateTime? UnavailableUntil,
    decimal UsedThisPeriod,
    decimal? MonthlyBudget,
    decimal? EstimatedRemainingCredits,
    DateTime? NextRetryTime,
    string? LastError,
    int ConsecutiveFailures,
    long SuccessCount,
    long FailureCount,
    long FailoverCount,
    DateTime? LastSuccessAt,
    DateTime UpdatedAt);

public static class SearchProviderEndpoints
{
    public static IEndpointRouteBuilder MapSearchProviders(this IEndpointRouteBuilder app)
    {
        MapRoutes(app.MapGroup("/admin/providers"));
        MapRoutes(app.MapGroup("/admin/api/search-providers"));
        return app;
    }

    /// <summary>Semua endpoint memakai peran admin (owner/admin); viewer tidak boleh melihat konfigurasi kunci.</summary>
    private static void MapRoutes(RouteGroupBuilder g)
    {
        g.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Json(new { error = new
                {
                    message = "Konfigurasi atau state berubah bersamaan. Muat ulang dan coba lagi.",
                    type = "invalid_request_error", code = "concurrent_update",
                } }, statusCode: StatusCodes.Status409Conflict);
            }
        });
        g.MapGet("", ListAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapGet("{id:guid}", GetAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("", CreateAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPut("{id:guid}", UpdateAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapDelete("{id:guid}", DeleteAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/enable", EnableAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/disable", DisableAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/reset", ResetAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapPost("{id:guid}/test", TestAsync).RequireAuthorization(Policies.TenantAdmin);
        g.MapGet("status", StatusAsync).RequireAuthorization(Policies.TenantAdmin);
    }

    private static async Task<IResult> ListAsync(GatewayDbContext db, CancellationToken ct)
    {
        var list = await db.SearchProviderConfigs.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => ToDto(c))
            .ToListAsync(ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(Guid id, GatewayDbContext db, CancellationToken ct)
    {
        var config = await db.SearchProviderConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PublicId == id, ct);
        if (config == null) throw GatewayException.NotFound("Penyedia pencarian tidak ditemukan.");
        return Results.Ok(ToDto(config));
    }

    private static async Task<IResult> CreateAsync(
        CreateSearchProviderRequest req,
        SearchProviderRegistry registry,
        AuditWriter audit,
        CancellationToken ct)
    {
        var config = await registry.CreateAsync(new SearchProviderSpec(
            req.Type, req.Name, req.BaseUri, req.ApiKey, req.Weight, req.Capabilities,
            req.MonthlyBudget, req.CostPerSearch, req.CostPerExtract, req.ResetPolicy, req.ErrorMappingOverride), ct);

        // Kunci API mentah dan override mentah tidak pernah dicatat.
        await audit.WriteAsync("search_provider.create", "search_provider", config.PublicId.ToString(),
            new { config.Type, config.Name });
        return Results.Created($"/admin/providers/{config.PublicId}", ToDto(config));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateSearchProviderRequest req,
        GatewayDbContext db,
        SearchProviderRegistry registry,
        ProviderKeyProtector protector,
        TimeProvider clock,
        AuditWriter audit,
        CancellationToken ct)
    {
        var config = await db.SearchProviderConfigs.FirstOrDefaultAsync(c => c.PublicId == id, ct);
        if (config == null) throw GatewayException.NotFound("Penyedia pencarian tidak ditemukan.");

        var changed = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            var name = SearchProviderRegistry.NormalizeName(req.Name, config.Type);
            if (name != config.Name) { config.Name = name; changed.Add("name"); }
        }
        if (!string.IsNullOrWhiteSpace(req.BaseUri))
        {
            var baseUri = registry.ResolveBaseUri(config.Type, req.BaseUri);
            if (baseUri != config.BaseUri) { config.BaseUri = baseUri; changed.Add("baseUri"); }
        }
        var keyChanged = !string.IsNullOrWhiteSpace(req.ApiKey);
        if (keyChanged)
        {
            var apiKey = SearchProviderRegistry.RequireApiKey(req.ApiKey);
            config.ApiKeyEncrypted = protector.Protect(db.CurrentTenantId, apiKey);
            config.KeyHint = ProviderKeyProtector.Hint(apiKey);
            changed.Add("apiKey");
        }
        var enabledToggle = req.IsEnabled.HasValue && req.IsEnabled.Value != config.IsEnabled;
        if (enabledToggle)
        {
            config.IsEnabled = req.IsEnabled!.Value;
            changed.Add(req.IsEnabled.Value ? "enable" : "disable");
        }
        if (req.Weight.HasValue)
        {
            var weight = SearchProviderRegistry.NormalizeWeight(req.Weight);
            if (weight != config.Weight) { config.Weight = weight; changed.Add("weight"); }
        }
        if (!string.IsNullOrWhiteSpace(req.Capabilities))
        {
            var capabilities = SearchProviderRegistry.NormalizeCapabilities(req.Capabilities);
            if (capabilities != config.Capabilities) { config.Capabilities = capabilities; changed.Add("capabilities"); }
        }
        if (req.ClearMonthlyBudget == true)
        {
            config.MonthlyBudget = null;
            changed.Add("monthlyBudget");
        }
        else if (req.MonthlyBudget.HasValue)
        {
            var budget = SearchProviderRegistry.NormalizeBudget(req.MonthlyBudget);
            if (budget != config.MonthlyBudget) { config.MonthlyBudget = budget; changed.Add("monthlyBudget"); }
        }
        if (req.CostPerSearch.HasValue)
        {
            var cost = SearchProviderRegistry.NormalizeCost(req.CostPerSearch, config.CostPerSearch);
            if (cost != config.CostPerSearch) { config.CostPerSearch = cost; changed.Add("costPerSearch"); }
        }
        if (req.CostPerExtract.HasValue)
        {
            var cost = SearchProviderRegistry.NormalizeCost(req.CostPerExtract, config.CostPerExtract);
            if (cost != config.CostPerExtract) { config.CostPerExtract = cost; changed.Add("costPerExtract"); }
        }
        if (!string.IsNullOrWhiteSpace(req.ResetPolicy))
        {
            var resetPolicy = SearchProviderRegistry.NormalizeResetPolicy(req.ResetPolicy);
            if (resetPolicy != config.ResetPolicy) { config.ResetPolicy = resetPolicy; changed.Add("resetPolicy"); }
        }
        if (req.ClearErrorMappingOverride == true)
        {
            config.ErrorMappingOverride = null;
            changed.Add("errorMappingOverride");
        }
        else if (req.ErrorMappingOverride is not null)
        {
            SearchProviderRegistry.ValidateErrorMapping(req.ErrorMappingOverride);
            config.ErrorMappingOverride = req.ErrorMappingOverride;
            changed.Add("errorMappingOverride");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        config.UpdatedAt = now;

        // Perubahan kunci / enable / disable membatalkan hasil request yang sedang berjalan: naikkan Generation
        // dan AdminGeneration serta lepas probe lease supaya hasil lama tidak menimpa keputusan admin.
        // Perubahan konfigurasi lain (mis. BaseUri) tetap menaikkan AdminGeneration agar kegagalan kunci lama
        // tidak menimpa state setelah konfigurasi berubah.
        var invalidate = keyChanged || enabledToggle;
        if (changed.Count > 0 || invalidate)
        {
            var state = await db.SearchProviderStates.FirstOrDefaultAsync(s => s.ProviderId == config.Id, ct);
            if (state is not null)
            {
                BumpState(state, now, bumpGeneration: invalidate);
                if (!config.IsEnabled)
                {
                    // Disabled tetap disabled walau kunci diganti.
                    state.Status = SearchProviderStatuses.Disabled;
                    state.UnavailableUntil = null;
                }
                else if (state.Status == SearchProviderStatuses.Disabled
                         || (keyChanged && state.Status == SearchProviderStatuses.Broken))
                {
                    // Kunci baru mengembalikan provider broken/disabled-mismatch ke available.
                    state.Status = SearchProviderStatuses.Available;
                    state.UnavailableUntil = null;
                    state.ConsecutiveFailures = 0;
                    state.LastError = null;
                }
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SearchProviderRegistry.IsUniqueViolation(ex))
        {
            throw SearchProviderRegistry.DuplicateName(config.Name);
        }

        if (changed.Count > 0)
            await audit.WriteAsync("search_provider.update", "search_provider", id.ToString(), new { fields = changed });
        return Results.Ok(ToDto(config));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, GatewayDbContext db, TimeProvider clock, AuditWriter audit, CancellationToken ct)
    {
        var config = await db.SearchProviderConfigs.FirstOrDefaultAsync(c => c.PublicId == id, ct);
        if (config == null) throw GatewayException.NotFound("Penyedia pencarian tidak ditemukan.");

        var now = clock.GetUtcNow().UtcDateTime;
        config.DeletedAt = now;
        config.IsEnabled = false;
        config.UpdatedAt = now;

        var state = await db.SearchProviderStates.FirstOrDefaultAsync(s => s.ProviderId == config.Id, ct);
        if (state is not null)
        {
            BumpState(state, now);
            state.Status = SearchProviderStatuses.Disabled;
            state.UnavailableUntil = null;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("search_provider.delete", "search_provider", id.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> EnableAsync(Guid id, GatewayDbContext db, TimeProvider clock, AuditWriter audit, CancellationToken ct)
    {
        var config = await db.SearchProviderConfigs.FirstOrDefaultAsync(c => c.PublicId == id, ct);
        if (config == null) throw GatewayException.NotFound("Penyedia pencarian tidak ditemukan.");

        var now = clock.GetUtcNow().UtcDateTime;
        config.IsEnabled = true;
        config.UpdatedAt = now;

        var state = await db.SearchProviderStates.FirstOrDefaultAsync(s => s.ProviderId == config.Id, ct);
        if (state is not null)
        {
            BumpState(state, now);
            if (state.Status == SearchProviderStatuses.Disabled)
            {
                state.Status = SearchProviderStatuses.Available;
                state.UnavailableUntil = null;
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("search_provider.enable", "search_provider", id.ToString());
        return Results.Ok(ToDto(config));
    }

    private static async Task<IResult> DisableAsync(Guid id, GatewayDbContext db, TimeProvider clock, AuditWriter audit, CancellationToken ct)
    {
        var config = await db.SearchProviderConfigs.FirstOrDefaultAsync(c => c.PublicId == id, ct);
        if (config == null) throw GatewayException.NotFound("Penyedia pencarian tidak ditemukan.");

        var now = clock.GetUtcNow().UtcDateTime;
        config.IsEnabled = false;
        config.UpdatedAt = now;

        var state = await db.SearchProviderStates.FirstOrDefaultAsync(s => s.ProviderId == config.Id, ct);
        if (state is not null)
        {
            BumpState(state, now);
            state.Status = SearchProviderStatuses.Disabled;
            state.UnavailableUntil = null;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("search_provider.disable", "search_provider", id.ToString());
        return Results.Ok(ToDto(config));
    }

    private static async Task<IResult> ResetAsync(Guid id, GatewayDbContext db, TimeProvider clock, AuditWriter audit, CancellationToken ct)
    {
        var config = await db.SearchProviderConfigs.FirstOrDefaultAsync(c => c.PublicId == id, ct);
        if (config == null) throw GatewayException.NotFound("Penyedia pencarian tidak ditemukan.");

        var state = await db.SearchProviderStates.FirstOrDefaultAsync(s => s.ProviderId == config.Id, ct);
        if (state is not null)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            config.UpdatedAt = now; // menyentuh config agar token konkurensi berlaku untuk reset
            BumpState(state, now);
            // Broken/rate-limited kembali segar, tetapi provider yang di-disable admin tetap disabled.
            state.Status = config.IsEnabled ? SearchProviderStatuses.Available : SearchProviderStatuses.Disabled;
            state.UnavailableUntil = null;
            state.ConsecutiveFailures = 0;
            state.LastError = null;
            if (config.ResetPolicy == ResetPolicies.Manual)
            {
                state.UsedThisPeriod = 0;
                state.PeriodStart = now;
            }
            await db.SaveChangesAsync(ct);
        }

        await audit.WriteAsync("search_provider.reset", "search_provider", id.ToString(), new { config.IsEnabled });
        return Results.Ok(new { status = "reset_successful", provider = config.Name });
    }

    private static async Task<IResult> TestAsync(Guid id, WebSearchHub hub, AuditWriter audit, CancellationToken ct)
    {
        // Lewat hub: satu panggilan murah yang tercatat di state/akuntansi; pesan selalu tersanitasi.
        var result = await hub.TestAsync(id, ct);
        await audit.WriteAsync("search_provider.test", "search_provider", id.ToString(), new { result.Success });
        return Results.Ok(new { success = result.Success, message = result.Message });
    }

    private static async Task<IResult> StatusAsync(GatewayDbContext db, TimeProvider clock,
        IOptions<WebSearchOptions> options, CancellationToken ct)
    {
        var configs = await db.SearchProviderConfigs.AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var configIds = configs.Select(c => c.Id).ToList();
        var states = await db.SearchProviderStates.AsNoTracking()
            .Where(s => configIds.Contains(s.ProviderId))
            .ToDictionaryAsync(s => s.ProviderId, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var result = new List<SearchProviderStatusDto>();
        foreach (var c in configs)
        {
            states.TryGetValue(c.Id, out var s);
            var periodStart = s?.PeriodStart ?? now;
            var rolledOver = c.ResetPolicy switch
            {
                ResetPolicies.FirstOfMonthUtc => periodStart < new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                ResetPolicies.Rolling30Days => periodStart.AddDays(30) <= now,
                _ => false,
            };
            var used = rolledOver ? 0m : s?.UsedThisPeriod ?? 0m;
            var nextReset = NextResetTime(c.ResetPolicy, rolledOver ? now : periodStart, now);
            var storedStatus = rolledOver && s?.Status == SearchProviderStatuses.Exhausted
                ? SearchProviderStatuses.Available : s?.Status ?? SearchProviderStatuses.Available;
            var activeProbe = s?.ProbeLeaseId is not null && s.ProbeLeaseUntil > now ? s.ProbeLeaseUntil : null;

            // Status dihitung ulang dari fakta terkini, bukan hanya kolom state: kuota habis dan cooldown yang
            // sudah lewat tidak boleh tampil seperti keadaan lama.
            var exhaustedByBudget = c.MonthlyBudget is { } budget && used >= budget * options.Value.BudgetThreshold;
            var cooldownOver = s?.UnavailableUntil is { } until && until <= now;
            var status = !c.IsEnabled || storedStatus == SearchProviderStatuses.Disabled ? SearchProviderStatuses.Disabled
                : storedStatus == SearchProviderStatuses.Broken ? SearchProviderStatuses.Broken
                : exhaustedByBudget ? SearchProviderStatuses.Exhausted
                : cooldownOver && activeProbe is null && storedStatus is SearchProviderStatuses.RateLimited or SearchProviderStatuses.Exhausted
                    ? SearchProviderStatuses.Available
                : storedStatus;

            DateTime? unavailableUntil = status switch
            {
                SearchProviderStatuses.Disabled or SearchProviderStatuses.Broken => null,
                SearchProviderStatuses.Exhausted when exhaustedByBudget => nextReset,
                SearchProviderStatuses.Available => null,
                _ => activeProbe is { } probe && (s?.UnavailableUntil is null || s.UnavailableUntil < probe)
                    ? probe : s?.UnavailableUntil,
            };
            var nextRetry = status is SearchProviderStatuses.RateLimited or SearchProviderStatuses.Exhausted
                ? unavailableUntil
                : null;

            result.Add(new SearchProviderStatusDto(
                c.PublicId,
                c.Name,
                c.Type,
                status,
                unavailableUntil,
                used,
                c.MonthlyBudget,
                c.MonthlyBudget.HasValue ? Math.Max(0, c.MonthlyBudget.Value - used) : null,
                nextRetry,
                s?.LastError,
                s?.ConsecutiveFailures ?? 0,
                s?.SuccessCount ?? 0,
                s?.FailureCount ?? 0,
                s?.FailoverCount ?? 0,
                s?.LastSuccessAt,
                s?.UpdatedAt ?? c.UpdatedAt));
        }

        return Results.Ok(result);
    }

    /// <summary>
    /// AdminGeneration++ selalu (setiap mutasi admin), Generation++ saat hasil lama harus dibatalkan
    /// (kunci/enable/disable/reset), dan probe lease dilepas: request yang sedang berjalan tidak menimpa keputusan admin.
    /// </summary>
    private static void BumpState(SearchProviderState state, DateTime now, bool bumpGeneration = true)
    {
        state.AdminGeneration++;
        if (bumpGeneration) state.Generation++;
        state.ProbeLeaseId = null;
        state.ProbeLeaseUntil = null;
        state.UpdatedAt = now;
    }

    private static DateTime? NextResetTime(string resetPolicy, DateTime periodStart, DateTime now) => resetPolicy switch
    {
        ResetPolicies.FirstOfMonthUtc => new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1),
        ResetPolicies.Rolling30Days => periodStart.AddDays(30),
        _ => null,
    };

    private static SearchProviderDto ToDto(SearchProviderConfig c) => new(
        c.PublicId, c.Name, c.Type, c.BaseUri, c.KeyHint, c.IsEnabled, c.Weight,
        c.Capabilities, c.MonthlyBudget, c.CostPerSearch, c.CostPerExtract, c.ResetPolicy,
        c.ErrorMappingOverride, c.CreatedAt, c.UpdatedAt);
}
