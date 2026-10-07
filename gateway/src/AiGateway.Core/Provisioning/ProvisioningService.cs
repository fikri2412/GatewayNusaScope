using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Proxy;
using AiGateway.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Provisioning;

public sealed record RouteSpec(long ProviderId, string UpstreamModel, int Priority = 0, int Weight = 1);

/// <summary>Satu model yang akan dibuat: alias untuk klien + target upstream + harga awal (opsional).</summary>
public sealed record ModelSpec(
    string Alias, string UpstreamModel, int? MaxOutputTokens = null,
    decimal? InputPricePer1M = null, decimal? OutputPricePer1M = null, string Currency = "USD",
    decimal? CacheReadPricePer1M = null, decimal? CacheWritePricePer1M = null, IReadOnlyList<PriceTier>? Tiers = null);

/// <summary>Kebijakan satu scope. NULL = tidak dibatasi; <see cref="AllowedModels"/> NULL = semua model.</summary>
public sealed record PolicySpec(
    int? MaxTokensPerRequest = null, int? RequestsPerMinute = null, long? DailyTokenQuota = null,
    long? MonthlyTokenQuota = null, decimal? MonthlyBudget = null, IReadOnlyCollection<string>? AllowedModels = null,
    bool Enabled = true);

/// <summary>Key klien yang baru dibuat; <see cref="PlaintextKey"/> hanya tersedia sekali ini.</summary>
public sealed record CreatedApiKey(ApiKey Entity, string PlaintextKey);

/// <summary>
/// Operasi pembuatan konfigurasi. Dipakai admin API (G3) dan seed dev. Operasi tingkat tenant memakai
/// <see cref="GatewayDbContext.CurrentTenantId"/> yang sudah diset pemanggil.
/// </summary>
public sealed partial class ProvisioningService(
    GatewayDbContext db, ProviderKeyProtector protector, IHttpClientFactory httpFactory, OutboundSecurityPolicy outbound)
{
    // --- tingkat platform ---------------------------------------------------------------------

    public async Task<Tenant> CreateTenantAsync(string name, string slug, long? planId, CancellationToken ct)
    {
        name = RequireName(name, 200, "name");
        if (!SlugRegex().IsMatch(slug))
            throw GatewayException.BadRequest("invalid_slug", "Slug harus huruf kecil, angka, atau '-', 1-63 karakter.");

        planId ??= (await db.Plans.FirstOrDefaultAsync(p => p.Name == DbSeeder.DefaultPlanName, ct))?.Id
            ?? throw GatewayException.BadRequest("no_plan", "Plan default belum ada.");
        if (!await db.Plans.AnyAsync(p => p.Id == planId, ct)) throw GatewayException.NotFound("Plan");

        var tenant = new Tenant { Name = name, Slug = slug, PlanId = planId.Value };
        db.Tenants.Add(tenant);
        await SaveUniqueAsync("Slug tenant sudah dipakai.", ct);
        return tenant;
    }

    // --- provider ------------------------------------------------------------------------------

    /// <summary>Provider dari template (mis. opencode): koneksi disalin dari template, key milik tenant.</summary>
    public async Task<Provider> CreateProviderFromTemplateAsync(
        string templateCode, string name, string apiKey, string? baseUrlOverride, CancellationToken ct)
    {
        var template = await db.ProviderTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Code == templateCode && t.Enabled, ct)
            ?? throw GatewayException.NotFound("Template provider");
        return await CreateProviderCoreAsync(template.Id, name, template.Type,
            baseUrlOverride ?? template.DefaultBaseUrl, template.AuthHeader, template.AuthPrefix, template.ModelsPath, apiKey, ct);
    }

    /// <summary>Provider custom (mis. CommandCode): URI, key, dan bila perlu nama header auth.</summary>
    public Task<Provider> CreateCustomProviderAsync(
        string name, string baseUrl, string apiKey, string? authHeader, string? authPrefix, string? modelsPath, CancellationToken ct) =>
        CreateProviderCoreAsync(null, name, ProviderTypes.OpenAi, baseUrl,
            authHeader ?? "Authorization", authPrefix ?? "Bearer ", modelsPath, apiKey, ct);

    private async Task<Provider> CreateProviderCoreAsync(
        long? templateId, string name, string type, string baseUrl, string authHeader, string authPrefix,
        string? modelsPath, string apiKey, CancellationToken ct)
    {
        name = RequireName(name, 100, "name");
        ValidateBaseUrl(baseUrl);
        ValidateAuth(authHeader, authPrefix);
        ValidateApiKey(apiKey);
        if (modelsPath is not null && !SafePathRegex().IsMatch(modelsPath))
            throw GatewayException.BadRequest("invalid_models_path", "models_path tidak valid.");

        var provider = new Provider
        {
            Name = name, TemplateId = templateId, Type = type, BaseUrl = baseUrl,
            AuthHeader = authHeader, AuthPrefix = authPrefix, ModelsPath = modelsPath,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Providers.Add(provider);
        await SaveUniqueAsync("Nama provider sudah dipakai.", ct);
        db.ProviderCredentials.Add(NewCredential(provider.Id, apiKey));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return provider;
    }

    /// <summary>Rotasi tanpa downtime: key lama dinonaktifkan, key baru aktif, dalam satu transaksi.</summary>
    public async Task RotateProviderKeyAsync(long providerId, string newApiKey, CancellationToken ct)
    {
        ValidateApiKey(newApiKey);
        if (!await db.Providers.AnyAsync(p => p.Id == providerId, ct)) throw GatewayException.NotFound("Provider");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var old in await db.ProviderCredentials.Where(c => c.ProviderId == providerId && c.Status == CredentialStatuses.Active).ToListAsync(ct))
        {
            old.Status = CredentialStatuses.Disabled;
            old.DisabledAt = now;
        }
        await db.SaveChangesAsync(ct); // indeks unik "satu key aktif" mensyaratkan yang lama nonaktif lebih dulu
        db.ProviderCredentials.Add(NewCredential(providerId, newApiKey));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private ProviderCredential NewCredential(long providerId, string apiKey) => new()
    {
        ProviderId = providerId,
        ApiKeyEncrypted = protector.Protect(db.CurrentTenantId, apiKey),
        KeyHint = ProviderKeyProtector.Hint(apiKey),
    };

    // --- model ---------------------------------------------------------------------------------

    /// <summary>Daftar id model dari provider (GET {base}/{models_path}); hanya menawarkan, tidak menyimpan.</summary>
    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(long providerId, CancellationToken ct)
    {
        var provider = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw GatewayException.NotFound("Provider");
        if (provider.ModelsPath is null)
            throw GatewayException.BadRequest("discovery_unsupported", "Provider ini tidak punya endpoint daftar model.");
        var credential = await db.ProviderCredentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProviderId == providerId && c.Status == CredentialStatuses.Active, ct)
            ?? throw GatewayException.BadRequest("no_credential", "Provider belum punya key aktif.");

        // Validasi ulang BaseUrl tersimpan (baris lama bisa saja belum lewat anti-SSRF); koneksi tetap dicek di handler.
        outbound.RequireHttpsUri(provider.BaseUrl, "invalid_base_url");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(provider.BaseUrl.TrimEnd('/') + "/"), provider.ModelsPath.TrimStart('/')));
        request.Headers.TryAddWithoutValidation(provider.AuthHeader,
            provider.AuthPrefix + protector.Unprotect(db.CurrentTenantId, credential.ApiKeyEncrypted));

        try
        {
            using var response = await httpFactory.CreateClient(ChatProxy.HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new GatewayException(502, "discovery_failed", $"Provider menjawab HTTP {(int)response.StatusCode}.");
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = root is JsonArray a ? a : root?["data"] as JsonArray;
            return (items ?? [])
                .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?["id"]?.GetValue<string>())
                .Where(id => id is { Length: > 0 and <= 200 })
                .Select(id => id!).Distinct().Take(1000).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new GatewayException(502, "discovery_failed", "Daftar model dari provider tidak bisa dibaca.");
        }
    }

    /// <summary>Model dari katalog template provider ini; <paramref name="upstreamModels"/> null = semua yang aktif.</summary>
    public async Task<List<ModelSpec>> CatalogSpecsAsync(long providerId, IReadOnlyCollection<string>? upstreamModels, CancellationToken ct)
    {
        var provider = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw GatewayException.NotFound("Provider");
        if (provider.TemplateId is null)
            throw GatewayException.BadRequest("no_catalog", "Provider custom tidak punya katalog; gunakan discover atau daftar manual.");

        // Gateway baru bisa meneruskan Chat Completions; keluarga lain disimpan di katalog tapi tidak ditawarkan.
        var query = db.CatalogModels.AsNoTracking()
            .Where(m => m.TemplateId == provider.TemplateId && m.Enabled && m.ApiFamily == ApiFamilies.OpenAiChat);
        if (upstreamModels is not null) query = query.Where(m => upstreamModels.Contains(m.UpstreamModel));
        return (await query.OrderBy(m => m.UpstreamModel).ToListAsync(ct)).Select(m =>
            new ModelSpec(m.UpstreamModel, m.UpstreamModel, m.MaxOutputTokens, m.InputPricePer1M, m.OutputPricePer1M, m.Currency,
                m.CacheReadPricePer1M, m.CacheWritePricePer1M,
                string.IsNullOrEmpty(m.ExtraTiersJson) ? null : JsonSerializer.Deserialize<List<PriceTier>>(m.ExtraTiersJson))).ToList();
    }

    /// <summary>
    /// Buat model + route (priority 0) + harga awal untuk tiap spec. Idempoten: alias yang sudah ada dilewati.
    /// </summary>
    public async Task<List<Model>> ImportModelsAsync(long providerId, IReadOnlyCollection<ModelSpec> specs, CancellationToken ct)
    {
        if (!await db.Providers.AnyAsync(p => p.Id == providerId, ct)) throw GatewayException.NotFound("Provider");
        var created = new List<Model>();
        foreach (var spec in specs)
        {
            if (await db.Models.AnyAsync(m => m.Alias == spec.Alias, ct)) continue;
            created.Add(await CreateModelAsync(spec, [new RouteSpec(providerId, spec.UpstreamModel)], ct));
        }
        return created;
    }

    public async Task<Model> CreateModelAsync(ModelSpec spec, IReadOnlyCollection<RouteSpec> routes, CancellationToken ct)
    {
        if (!AliasRegex().IsMatch(spec.Alias))
            throw GatewayException.BadRequest("invalid_alias", "Alias hanya huruf, angka, '.', '_', ':', '/', '-' (maks 100 karakter).");
        if (routes.Count == 0) throw GatewayException.BadRequest("no_routes", "Model butuh minimal satu route.");
        if (spec.MaxOutputTokens is <= 0) throw GatewayException.BadRequest("invalid_max_output", "max_output_tokens harus > 0.");
        if (spec.InputPricePer1M is < 0 || spec.OutputPricePer1M is < 0)
            throw GatewayException.BadRequest("invalid_price", "Harga tidak boleh negatif.");

        var providerIds = routes.Select(r => r.ProviderId).Distinct().ToList();
        if (await db.Providers.CountAsync(p => providerIds.Contains(p.Id), ct) != providerIds.Count)
            throw GatewayException.NotFound("Provider");
        foreach (var r in routes)
        {
            if (string.IsNullOrWhiteSpace(r.UpstreamModel) || r.UpstreamModel.Length > 200)
                throw GatewayException.BadRequest("invalid_upstream_model", "upstream_model tidak valid.");
            if (r.Priority < 0 || r.Weight < 1)
                throw GatewayException.BadRequest("invalid_route", "priority >= 0 dan weight >= 1.");
        }

        var model = new Model { Alias = spec.Alias, MaxOutputTokens = spec.MaxOutputTokens };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Models.Add(model);
        await SaveUniqueAsync("Alias model sudah dipakai.", ct);
        foreach (var r in routes)
            db.ModelRoutes.Add(new ModelRoute { ModelId = model.Id, ProviderId = r.ProviderId, UpstreamModel = r.UpstreamModel, Priority = r.Priority, Weight = r.Weight });
        if (spec.InputPricePer1M is not null || spec.OutputPricePer1M is not null)
        {
            var effective = DateTime.UtcNow;
            db.ModelPrices.Add(new ModelPrice
            {
                ModelId = model.Id, InputPricePer1M = spec.InputPricePer1M ?? 0, OutputPricePer1M = spec.OutputPricePer1M ?? 0,
                CacheReadPricePer1M = spec.CacheReadPricePer1M, CacheWritePricePer1M = spec.CacheWritePricePer1M,
                Currency = spec.Currency, EffectiveFrom = effective,
            });
            foreach (var tier in spec.Tiers ?? [])
                db.ModelPrices.Add(new ModelPrice
                {
                    ModelId = model.Id, InputPricePer1M = tier.Input, OutputPricePer1M = tier.Output,
                    CacheReadPricePer1M = tier.CacheRead, CacheWritePricePer1M = tier.CacheWrite,
                    Currency = spec.Currency, EffectiveFrom = effective, MinInputTokens = tier.MinInputTokens,
                });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return model;
    }

    // --- project dan key klien -----------------------------------------------------------------

    public async Task<Project> CreateProjectAsync(string name, CancellationToken ct)
    {
        var projectName = RequireName(name, 100, "name");
        if ((await PlanAsync(ct)).MaxProjects is { } maxProjects && await db.Projects.CountAsync(ct) >= maxProjects)
            throw PlanLimit("project", maxProjects);
        var project = new Project { Name = projectName };
        db.Projects.Add(project);
        await SaveUniqueAsync("Nama project sudah dipakai.", ct);
        return project;
    }

    public async Task<CreatedApiKey> CreateApiKeyAsync(
        long projectId, string name, DateTime? expiresAt, IReadOnlyCollection<string>? allowedIps, long? createdByUserId, CancellationToken ct)
    {
        name = RequireName(name, 100, "name");
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw GatewayException.NotFound("Project");
        if ((await PlanAsync(ct)).MaxApiKeys is { } maxKeys && await db.ApiKeys.CountAsync(k => k.RevokedAt == null, ct) >= maxKeys)
            throw PlanLimit("API key aktif", maxKeys);
        if (expiresAt is not null && expiresAt <= DateTime.UtcNow)
            throw GatewayException.BadRequest("invalid_expiry", "Tanggal kedaluwarsa harus di masa depan.");
        if (allowedIps is { Count: > 0 })
            foreach (var entry in allowedIps)
                if (!(entry.Contains('/') ? System.Net.IPNetwork.TryParse(entry, out _) : System.Net.IPAddress.TryParse(entry, out _)))
                    throw GatewayException.BadRequest("invalid_ip", $"'{entry}' bukan IP atau CIDR yang valid.");

        var (plaintext, prefix, hash) = ApiKeyGenerator.Generate();
        var key = new ApiKey
        {
            ProjectId = projectId, Name = name, KeyPrefix = prefix, KeyHash = hash, ExpiresAt = expiresAt,
            AllowedIpsJson = allowedIps is { Count: > 0 } ? JsonSerializer.Serialize(allowedIps) : null,
            CreatedByUserId = createdByUserId,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return new CreatedApiKey(key, plaintext);
    }

    // --- kebijakan dan batas plan --------------------------------------------------------------

    /// <summary>Buat atau ubah kebijakan untuk satu scope. NULL = tidak dibatasi; daftar model NULL = semua model.</summary>
    public async Task<Policy> SetPolicyAsync(string scope, long scopeId, PolicySpec spec, CancellationToken ct)
    {
        var exists = scope switch
        {
            PolicyScopes.Tenant => scopeId == db.CurrentTenantId,
            PolicyScopes.Project => await db.Projects.AnyAsync(p => p.Id == scopeId, ct),
            PolicyScopes.Key => await db.ApiKeys.AnyAsync(k => k.Id == scopeId, ct),
            _ => throw GatewayException.BadRequest("invalid_scope", "scope harus tenant, project, atau key."),
        };
        if (!exists) throw GatewayException.NotFound("Target kebijakan");

        if (spec.MaxTokensPerRequest is < 1 || spec.RequestsPerMinute is < 1 || spec.DailyTokenQuota is < 1
            || spec.MonthlyTokenQuota is < 1 || spec.MonthlyBudget is <= 0)
            throw GatewayException.BadRequest("invalid_limit", "Batas harus lebih besar dari 0 (kosongkan untuk tanpa batas).");
        if (spec.AllowedModels is not null
            && (spec.AllowedModels.Count is 0 or > 200 || spec.AllowedModels.Any(m => !AliasRegex().IsMatch(m))))
            throw GatewayException.BadRequest("invalid_allowed_models", "Daftar model harus 1-200 alias yang valid (kosongkan untuk semua model).");

        var policy = await db.Policies.FirstOrDefaultAsync(p => p.Scope == scope && p.ScopeId == scopeId, ct);
        if (policy is null) db.Policies.Add(policy = new Policy { Scope = scope, ScopeId = scopeId });
        policy.MaxTokensPerRequest = spec.MaxTokensPerRequest;
        policy.RequestsPerMinute = spec.RequestsPerMinute;
        policy.DailyTokenQuota = spec.DailyTokenQuota;
        policy.MonthlyTokenQuota = spec.MonthlyTokenQuota;
        policy.MonthlyBudget = spec.MonthlyBudget;
        policy.AllowedModelsJson = spec.AllowedModels is null ? null : JsonSerializer.Serialize(spec.AllowedModels);
        policy.Enabled = spec.Enabled;
        await SaveUniqueAsync("Kebijakan untuk target ini baru saja dibuat oleh proses lain.", ct);
        return policy;
    }

    private async Task<Plan> PlanAsync(CancellationToken ct) =>
        await (from t in db.Tenants where t.Id == db.CurrentTenantId
               join p in db.Plans on t.PlanId equals p.Id select p).FirstAsync(ct);

    private static GatewayException PlanLimit(string what, int max) =>
        new(403, "plan_limit_reached", $"Plan Anda hanya mengizinkan {max} {what}.");

    // --- validasi ------------------------------------------------------------------------------

    private async Task SaveUniqueAsync(string conflictMessage, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            throw GatewayException.Conflict("already_exists", conflictMessage);
        }
    }

    private static string RequireName(string value, int max, string field)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value.Length > max)
            throw GatewayException.BadRequest("invalid_" + field, $"{field} wajib diisi, maksimal {max} karakter.");
        return value;
    }

    /// <summary>Validasi statis anti-SSRF (https, tanpa user/query/fragment, literal privat ditolak) saat provider dikonfigurasi.</summary>
    private void ValidateBaseUrl(string baseUrl) => outbound.RequireHttpsUri(baseUrl, "invalid_base_url");

    private static void ValidateAuth(string header, string prefix)
    {
        if (!HeaderNameRegex().IsMatch(header))
            throw GatewayException.BadRequest("invalid_auth_header", "Nama header auth tidak valid.");
        if (prefix.Length > 30 || prefix.Any(c => c < ' ' || c > '~'))
            throw GatewayException.BadRequest("invalid_auth_prefix", "Prefix auth tidak valid.");
    }

    private static void ValidateApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 500 || apiKey.Any(c => c < ' ' || c > '~'))
            throw GatewayException.BadRequest("invalid_api_key", "API key provider kosong, terlalu panjang, atau berisi karakter tak valid.");
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")] private static partial Regex SlugRegex();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$")] private static partial Regex AliasRegex();
    [GeneratedRegex("^[A-Za-z0-9-]{1,100}$")] private static partial Regex HeaderNameRegex();
    // Path relatif tanpa awalan '/' dan tanpa segmen '..' (tidak boleh naik keluar dari base_url).
    [GeneratedRegex(@"^(?!.*\.\.)[A-Za-z0-9_~-][A-Za-z0-9._~/-]{0,199}$")] private static partial Regex SafePathRegex();
}
