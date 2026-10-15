using System.Text.Json;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.WebSearch;

/// <summary>
/// Input registrasi penyedia. Hanya <see cref="Type"/> dan <see cref="ApiKey"/> yang wajib: nama default = tipe,
/// BaseUri default per tipe yang dikenal.
/// </summary>
public sealed record SearchProviderSpec(
    string? Type = null,
    string? Name = null,
    string? BaseUri = null,
    string? ApiKey = null,
    int? Weight = null,
    string? Capabilities = null,
    decimal? MonthlyBudget = null,
    decimal? CostPerSearch = null,
    decimal? CostPerExtract = null,
    string? ResetPolicy = null,
    string? ErrorMappingOverride = null);

/// <summary>
/// Satu-satunya jalur validasi + default registrasi penyedia (admin API dan seeder). Config dan state selalu
/// dibuat bersama dalam satu transaksi supaya tidak mungkin ada config tanpa state.
/// </summary>
public sealed class SearchProviderRegistry(
    GatewayDbContext db,
    ProviderKeyProtector protector,
    OutboundSecurityPolicy outbound,
    TimeProvider clock,
    IEnumerable<ISearchProvider> adapters)
{
    public const int NameMaxLength = 100;
    public const int ApiKeyMaxLength = 1000;
    public const int CapabilitiesMaxLength = 100;
    public const int ErrorMappingMaxLength = 2000;
    public const int WeightMin = 1;
    public const int WeightMax = 100;
    public const decimal CostMax = 10_000m;
    public const decimal BudgetMax = 1_000_000m;
    private const int ErrorMappingMaxRules = 50;
    private const int ErrorMappingMaxSubstring = 200;

    private static readonly string[] ErrorMappingRuleNames =
        ["exhaustedStatusCodes", "exhaustedBodySubstrings", "rateLimitStatusCodes", "brokenStatusCodes"];

    public async Task<SearchProviderConfig> CreateAsync(SearchProviderSpec spec, CancellationToken ct)
    {
        var type = NormalizeType(spec.Type);
        var name = NormalizeName(spec.Name, type);
        var baseUri = ResolveBaseUri(type, spec.BaseUri);
        var apiKey = RequireApiKey(spec.ApiKey);
        var weight = NormalizeWeight(spec.Weight);
        var capabilities = NormalizeCapabilities(spec.Capabilities);
        var resetPolicy = NormalizeResetPolicy(spec.ResetPolicy);
        ValidateErrorMapping(spec.ErrorMappingOverride);
        var budget = NormalizeBudget(spec.MonthlyBudget);
        var costSearch = NormalizeCost(spec.CostPerSearch, 1m);
        var costExtract = NormalizeCost(spec.CostPerExtract, 1m);

        if (db.CurrentTenantId == GatewayDbContext.NoTenant)
            throw new InvalidOperationException("SearchProviderRegistry: konteks tenant belum dipasang.");
        if (await db.SearchProviderConfigs.AnyAsync(c => c.Name == name, ct))
            throw DuplicateName(name);

        var now = clock.GetUtcNow().UtcDateTime;
        var config = new SearchProviderConfig
        {
            Name = name,
            Type = type,
            BaseUri = baseUri,
            ApiKeyEncrypted = protector.Protect(db.CurrentTenantId, apiKey),
            KeyHint = ProviderKeyProtector.Hint(apiKey),
            IsEnabled = true,
            Weight = weight,
            Capabilities = capabilities,
            MonthlyBudget = budget,
            CostPerSearch = costSearch,
            CostPerExtract = costExtract,
            ResetPolicy = resetPolicy,
            ErrorMappingOverride = spec.ErrorMappingOverride,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var state = new SearchProviderState
        {
            Status = SearchProviderStatuses.Available,
            PeriodStart = now,
            UpdatedAt = now,
        };

        // ProviderId butuh identitas config dari database; satu transaksi memastikan keduanya lahir bersama.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        db.SearchProviderConfigs.Add(config);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw DuplicateName(name);
        }
        state.ProviderId = config.Id;
        state.TenantId = config.TenantId;
        db.SearchProviderStates.Add(state);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return config;
    }

    /// <summary>Tipe divalidasi terhadap adapter yang terdaftar, bukan daftar konstanta, supaya adapter baru tidak butuh perubahan kode di sini.</summary>
    public string NormalizeType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw Bad("invalid_type", "Tipe penyedia wajib diisi.");
        var type = raw.Trim().ToLowerInvariant();
        if (!SearchProviderTypes.All.Contains(type)
            || !adapters.Any(a => a.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
        {
            var supported = string.Join(", ", adapters.Select(a => a.Type.ToLowerInvariant()).Distinct().Order());
            throw Bad("invalid_type", $"Tipe '{raw}' tidak didukung. Tipe yang tersedia: {supported}.");
        }
        return type;
    }

    public static string NormalizeName(string? raw, string type)
    {
        var name = raw?.Trim();
        if (string.IsNullOrEmpty(name)) name = type;
        if (name.Length > NameMaxLength)
            throw Bad("invalid_name", $"Nama penyedia maksimal {NameMaxLength} karakter.");
        return name;
    }

    public string ResolveBaseUri(string type, string? raw)
    {
        var uri = raw?.Trim();
        if (string.IsNullOrEmpty(uri))
        {
            // Default datang dari metadata adapter, bukan tabel per-tipe: adapter baru tidak butuh perubahan di sini.
            uri = adapters.FirstOrDefault(a => a.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.DefaultBaseUri;
            if (string.IsNullOrEmpty(uri))
                throw Bad("invalid_base_uri", $"BaseUri wajib diisi untuk tipe '{type}'.");
        }
        outbound.RequireHttpsUri(uri, "invalid_base_uri");
        return uri;
    }

    public static int NormalizeWeight(int? raw)
    {
        var weight = raw ?? 1;
        if (weight is < WeightMin or > WeightMax)
            throw Bad("invalid_weight", $"Weight harus antara {WeightMin} dan {WeightMax}.");
        return weight;
    }

    public static string NormalizeCapabilities(string? raw)
    {
        var parts = (string.IsNullOrWhiteSpace(raw) ? "search,extract" : raw)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToHashSet();
        if (parts.Count == 0 || parts.Any(p => p is not ("search" or "extract")))
            throw Bad("invalid_capabilities", "Capabilities hanya boleh berisi 'search' dan/atau 'extract'.");
        var canonical = string.Join(',', new[] { "search", "extract" }.Where(parts.Contains));
        if (canonical.Length > CapabilitiesMaxLength)
            throw Bad("invalid_capabilities", $"Capabilities maksimal {CapabilitiesMaxLength} karakter.");
        return canonical;
    }

    public static string NormalizeResetPolicy(string? raw)
    {
        var policy = string.IsNullOrWhiteSpace(raw) ? ResetPolicies.FirstOfMonthUtc : raw.Trim().ToLowerInvariant();
        if (!ResetPolicies.All.Contains(policy))
            throw Bad("invalid_reset_policy", $"Kebijakan reset '{raw}' tidak valid. Pilihan: {string.Join(", ", ResetPolicies.All)}.");
        return policy;
    }

    public static decimal? NormalizeBudget(decimal? raw)
    {
        if (raw is not { } value) return null;
        if (value < 0 || value > BudgetMax)
            throw Bad("invalid_budget", $"MonthlyBudget harus antara 0 dan {BudgetMax}.");
        return value;
    }

    public static decimal NormalizeCost(decimal? raw, decimal fallback)
    {
        var value = raw ?? fallback;
        if (value < 0 || value > CostMax)
            throw Bad("invalid_cost", $"Biaya harus antara 0 dan {CostMax}.");
        return value;
    }

    public static string RequireApiKey(string? raw)
    {
        var key = raw ?? "";
        if (string.IsNullOrWhiteSpace(key))
            throw Bad("invalid_api_key", "Kunci API wajib diisi.");
        if (key.Length > ApiKeyMaxLength)
            throw Bad("invalid_api_key", $"Kunci API maksimal {ApiKeyMaxLength} karakter.");
        return key;
    }

    /// <summary>
    /// Aturan JSON ketat: objek dengan minimal satu aturan, hanya kunci <see cref="ErrorMappingRules"/>, array
    /// berisi kode status 100-599 atau substring non-kosong, panjang per array/elemen/keseluruhan dibatasi.
    /// </summary>
    public static void ValidateErrorMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        if (json.Length > ErrorMappingMaxLength)
            throw Bad("invalid_error_mapping", $"ErrorMappingOverride maksimal {ErrorMappingMaxLength} karakter.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw Bad("invalid_error_mapping", "ErrorMappingOverride harus berupa JSON valid."); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.EnumerateObject().Any())
                throw Bad("invalid_error_mapping", "ErrorMappingOverride harus objek JSON berisi minimal satu aturan.");

            foreach (var rule in doc.RootElement.EnumerateObject())
            {
                var name = ErrorMappingRuleNames.FirstOrDefault(n => n.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw Bad("invalid_error_mapping", $"Kunci aturan '{rule.Name}' tidak dikenal.");
                var isStatusCodes = name != "exhaustedBodySubstrings";
                if (rule.Value.ValueKind != JsonValueKind.Array)
                    throw Bad("invalid_error_mapping", $"Aturan '{name}' harus berupa array.");
                if (rule.Value.GetArrayLength() == 0 || rule.Value.GetArrayLength() > ErrorMappingMaxRules)
                    throw Bad("invalid_error_mapping", $"Aturan '{name}' harus berisi 1-{ErrorMappingMaxRules} elemen.");

                foreach (var item in rule.Value.EnumerateArray())
                {
                    if (isStatusCodes)
                    {
                        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var code) || code is < 100 or > 599)
                            throw Bad("invalid_error_mapping", $"Aturan '{name}' hanya boleh berisi kode status 100-599.");
                        continue;
                    }

                    var substring = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (string.IsNullOrEmpty(substring) || substring.Length > ErrorMappingMaxSubstring)
                        throw Bad("invalid_error_mapping", $"Aturan '{name}' hanya boleh berisi substring 1-{ErrorMappingMaxSubstring} karakter.");
                }
            }
        }
    }

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    public static GatewayException DuplicateName(string name) =>
        new(StatusCodes.Status409Conflict, "provider_exists", $"Penyedia dengan nama '{name}' sudah ada.");

    private static GatewayException Bad(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);
}
