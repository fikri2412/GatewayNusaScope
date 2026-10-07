using System.Text.Json;
using AiGateway.Core.Common;
using AiGateway.Core.Domain;
using AiGateway.Core.Proxy;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Provisioning;

public sealed record ProviderPatch(
    string? Name = null, string? BaseUrl = null, bool? Enabled = null, string? AuthHeader = null, string? AuthPrefix = null,
    string? ModelsPath = null, bool ClearModelsPath = false);

public sealed record ModelPatch(string? Description = null, bool? Enabled = null, int? MaxOutputTokens = null, bool ClearMaxOutputTokens = false);

public sealed record ProjectPatch(
    string? Name = null, string? Status = null, bool? LogContent = null, int? ContentRetentionDays = null, bool ClearRetention = false);

public sealed record ApiKeyPatch(
    string? Name = null, IReadOnlyCollection<string>? AllowedIps = null, DateTime? ExpiresAt = null, bool ClearExpiry = false);

/// <summary>Harga baru untuk sebuah model; menjadi harga berlaku sejak sekarang (riwayat lama tetap).</summary>
public sealed record PriceSet(
    decimal Input, decimal Output, decimal? CacheRead = null, decimal? CacheWrite = null, string Currency = "USD",
    IReadOnlyList<PriceTier>? Tiers = null);

// Operasi ubah/hapus untuk admin API. Semua berjalan dalam tenant konteks; id yang bukan milik tenant = NotFound.
public sealed partial class ProvisioningService
{
    // --- provider ------------------------------------------------------------------------------

    public async Task<Provider> UpdateProviderAsync(long id, ProviderPatch patch, CancellationToken ct)
    {
        var p = await db.Providers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Provider");
        if (patch.Name is not null) p.Name = RequireName(patch.Name, 100, "name");
        if (patch.BaseUrl is not null) { ValidateBaseUrl(patch.BaseUrl); p.BaseUrl = patch.BaseUrl; }
        if (patch.AuthHeader is not null || patch.AuthPrefix is not null)
        {
            var header = patch.AuthHeader ?? p.AuthHeader;
            var prefix = patch.AuthPrefix ?? p.AuthPrefix;
            ValidateAuth(header, prefix);
            (p.AuthHeader, p.AuthPrefix) = (header, prefix);
        }
        if (patch.ClearModelsPath) p.ModelsPath = null;
        else if (patch.ModelsPath is not null)
        {
            if (!SafePathRegex().IsMatch(patch.ModelsPath)) throw GatewayException.BadRequest("invalid_models_path", "models_path tidak valid.");
            p.ModelsPath = patch.ModelsPath;
        }
        if (patch.Enabled is not null) p.Enabled = patch.Enabled.Value;
        await SaveConcurrentAsync("Nama provider sudah dipakai.", ct);
        return p;
    }

    /// <summary>Hapus lunak. Ditolak bila masih dipakai route model yang aktif.</summary>
    public async Task DeleteProviderAsync(long id, CancellationToken ct)
    {
        var p = await db.Providers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Provider");
        var inUse = await (from r in db.ModelRoutes join m in db.Models on r.ModelId equals m.Id
                           where r.ProviderId == id select r.Id).AnyAsync(ct);
        if (inUse) throw GatewayException.Conflict("in_use", "Provider masih dipakai route model. Pindahkan atau hapus route tersebut dulu.");

        p.DeletedAt = DateTime.UtcNow;
        p.Enabled = false;
        foreach (var c in await db.ProviderCredentials.Where(c => c.ProviderId == id && c.Status == CredentialStatuses.Active).ToListAsync(ct))
        {
            c.Status = CredentialStatuses.Disabled;
            c.DisabledAt = p.DeletedAt;
        }
        await db.SaveChangesAsync(ct);
    }

    // --- model ---------------------------------------------------------------------------------

    public async Task<Model> UpdateModelAsync(long id, ModelPatch patch, CancellationToken ct)
    {
        var m = await db.Models.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Model");
        if (patch.Description is not null) m.Description = ValidateDescription(patch.Description);
        if (patch.ClearMaxOutputTokens) m.MaxOutputTokens = null;
        else if (patch.MaxOutputTokens is { } max)
            m.MaxOutputTokens = max > 0 ? max : throw GatewayException.BadRequest("invalid_max_output", "max_output_tokens harus > 0.");
        if (patch.Enabled is not null) m.Enabled = patch.Enabled.Value;
        await SaveConcurrentAsync("Konflik saat menyimpan model.", ct);
        return m;
    }

    /// <summary>Ganti seluruh route model (urutan percobaan/fallback) secara atomik.</summary>
    public async Task ReplaceRoutesAsync(long modelId, IReadOnlyCollection<RouteSpec> routes, CancellationToken ct)
    {
        if (!await db.Models.AnyAsync(m => m.Id == modelId, ct)) throw GatewayException.NotFound("Model");
        await ValidateRoutesAsync(routes, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ModelRoutes.RemoveRange(await db.ModelRoutes.Where(r => r.ModelId == modelId).ToListAsync(ct));
        foreach (var r in routes)
            db.ModelRoutes.Add(new ModelRoute { ModelId = modelId, ProviderId = r.ProviderId, UpstreamModel = r.UpstreamModel, Priority = r.Priority, Weight = r.Weight });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task AddPriceSetAsync(long modelId, PriceSet price, CancellationToken ct)
    {
        if (!await db.Models.AnyAsync(m => m.Id == modelId, ct)) throw GatewayException.NotFound("Model");
        ValidatePrices(price.Input, price.Output, price.CacheRead, price.CacheWrite, price.Currency, price.Tiers);

        var effective = DateTime.UtcNow;
        db.ModelPrices.Add(new ModelPrice
        {
            ModelId = modelId, InputPricePer1M = price.Input, OutputPricePer1M = price.Output, CacheReadPricePer1M = price.CacheRead,
            CacheWritePricePer1M = price.CacheWrite, Currency = price.Currency, EffectiveFrom = effective,
        });
        foreach (var t in price.Tiers ?? [])
            db.ModelPrices.Add(new ModelPrice
            {
                ModelId = modelId, InputPricePer1M = t.Input, OutputPricePer1M = t.Output, CacheReadPricePer1M = t.CacheRead,
                CacheWritePricePer1M = t.CacheWrite, Currency = price.Currency, EffectiveFrom = effective, MinInputTokens = t.MinInputTokens,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteModelAsync(long id, CancellationToken ct)
    {
        var m = await db.Models.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Model");
        m.DeletedAt = DateTime.UtcNow;
        m.Enabled = false;
        await db.SaveChangesAsync(ct);
    }

    // --- project dan key -----------------------------------------------------------------------

    public async Task<Project> UpdateProjectAsync(long id, ProjectPatch patch, CancellationToken ct)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Project");
        if (patch.Name is not null) p.Name = RequireName(patch.Name, 100, "name");
        if (patch.Status is not null)
            p.Status = patch.Status is ProjectStatuses.Active or ProjectStatuses.Suspended
                ? patch.Status : throw GatewayException.BadRequest("invalid_status", "status harus active atau suspended.");
        if (patch.LogContent is not null) p.LogContent = patch.LogContent.Value;
        if (patch.ClearRetention) p.ContentRetentionDays = null;
        else if (patch.ContentRetentionDays is { } days) p.ContentRetentionDays = ValidateRetention(days);
        await SaveConcurrentAsync("Nama project sudah dipakai.", ct);
        return p;
    }

    /// <summary>Hapus lunak project dan cabut semua key-nya.</summary>
    public async Task DeleteProjectAsync(long id, CancellationToken ct)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Project");
        var now = DateTime.UtcNow;
        p.DeletedAt = now;
        foreach (var k in await db.ApiKeys.Where(k => k.ProjectId == id && k.RevokedAt == null).ToListAsync(ct)) k.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeApiKeyAsync(long id, CancellationToken ct)
    {
        var k = await db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("API key");
        k.RevokedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ApiKey> UpdateApiKeyAsync(long id, ApiKeyPatch patch, CancellationToken ct)
    {
        var k = await db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("API key");
        if (patch.Name is not null) k.Name = RequireName(patch.Name, 100, "name");
        if (patch.AllowedIps is not null)
        {
            foreach (var entry in patch.AllowedIps)
                if (!(entry.Contains('/') ? System.Net.IPNetwork.TryParse(entry, out _) : System.Net.IPAddress.TryParse(entry, out _)))
                    throw GatewayException.BadRequest("invalid_ip", $"'{entry}' bukan IP atau CIDR yang valid.");
            k.AllowedIpsJson = patch.AllowedIps.Count == 0 ? null : JsonSerializer.Serialize(patch.AllowedIps);
        }
        if (patch.ClearExpiry) k.ExpiresAt = null;
        else if (patch.ExpiresAt is { } expires)
        {
            expires = ToUtc(expires);
            k.ExpiresAt = expires > DateTime.UtcNow ? expires : throw GatewayException.BadRequest("invalid_expiry", "Tanggal kedaluwarsa harus di masa depan.");
        }
        await db.SaveChangesAsync(ct);
        return k;
    }

    public async Task DeletePolicyAsync(string scope, long scopeId, CancellationToken ct)
    {
        var p = await db.Policies.FirstOrDefaultAsync(x => x.Scope == scope && x.ScopeId == scopeId, ct)
            ?? throw GatewayException.NotFound("Kebijakan");
        db.Policies.Remove(p);
        await db.SaveChangesAsync(ct);
    }

    // --- bersama -------------------------------------------------------------------------------

    private async Task ValidateRoutesAsync(IReadOnlyCollection<RouteSpec> routes, CancellationToken ct)
    {
        if (routes.Count == 0) throw GatewayException.BadRequest("no_routes", "Model butuh minimal satu route.");
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
    }

    /// <summary>Simpan; konflik versi (row_version) dan nama ganda menjadi 409 yang bisa dipahami klien.</summary>
    private async Task SaveConcurrentAsync(string uniqueMessage, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw GatewayException.Conflict("concurrent_update", "Data diubah pengguna lain. Muat ulang lalu coba lagi.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            throw GatewayException.Conflict("already_exists", uniqueMessage);
        }
    }
}
