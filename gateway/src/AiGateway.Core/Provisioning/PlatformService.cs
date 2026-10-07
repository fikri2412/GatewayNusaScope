using System.Text.Json;
using System.Text.RegularExpressions;
using AiGateway.Core.Auth;
using AiGateway.Core.Catalog;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Proxy;
using AiGateway.Core.Reports;
using AiGateway.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Provisioning;

/// <summary>Isi satu plan. Batas <c>null</c> = tanpa batas.</summary>
public sealed record PlanSpec(string? Name, int? MaxProjects, int? MaxApiKeys, long? MaxRequestsPerMonth, long? MaxTokensPerMonth);

/// <summary>Perubahan tenant oleh platform admin; properti yang null dibiarkan.</summary>
public sealed record TenantPatch(string? Name = null, string? Status = null, long? PlanId = null);

/// <summary>Isi satu template provider; <see cref="Code"/>, <see cref="Name"/>, dan <see cref="DefaultBaseUrl"/> wajib.</summary>
public sealed record TemplateSpec(
    string? Code, string? Name, string? Type, string? DefaultBaseUrl, string? AuthHeader, string? AuthPrefix,
    string? ModelsPath, string? SyncKind, string? SyncUrl, bool Enabled);

/// <summary>Isi satu model katalog; nilai null dikosongkan dan <c>source</c> selalu dipaksa <c>manual</c>.</summary>
public sealed record CatalogModelSpec(
    string? TemplateCode, string? UpstreamModel, string? DisplayName, string? ApiFamily, int? ContextWindow,
    int? MaxInputTokens, int? MaxOutputTokens, decimal? InputPricePer1M, decimal? OutputPricePer1M,
    decimal? CacheReadPricePer1M, decimal? CacheWritePricePer1M, IReadOnlyList<PriceTier>? Tiers, string? Currency,
    bool SupportsTools, bool SupportsVision, bool SupportsReasoning, bool Enabled);

/// <summary>Tenant + nama plan-nya untuk DTO.</summary>
public sealed record TenantRow(Tenant Tenant, string PlanName);

/// <summary>Satu baris audit tingkat platform (tanpa <c>detail</c> agar metadata saja yang keluar).</summary>
public sealed record PlatformAuditRow(long Id, long? UserId, string Action, string? Entity, string? EntityId, string? Ip, DateTime CreatedAt);

/// <summary>
/// Operasi tingkat platform (<c>platform_admin</c>): plan, tenant beserta owner, katalog master, dan ringkasan
/// lintas tenant. Berjalan dengan konteks tanpa tenant (query filter fail-closed), jadi data tenant lain hanya
/// dibaca lewat jalur eksplisit: tabel tanpa filter, operator IgnoreQueryFilters, atau konteks tenant sementara.
/// </summary>
public sealed partial class PlatformService(
    GatewayDbContext db, ProvisioningService provisioning, UserAdminService users, UsageReports reports, OpenCodeCatalogSync sync,
    OutboundSecurityPolicy outbound)
{
    // --- plan ----------------------------------------------------------------------------------

    public Task<List<Plan>> ListPlansAsync(CancellationToken ct) =>
        db.Plans.AsNoTracking().OrderBy(p => p.Name).Take(500).ToListAsync(ct);

    public async Task<Plan> CreatePlanAsync(PlanSpec spec, CancellationToken ct)
    {
        var plan = new Plan { Name = RequireName(spec.Name, 100, "name") };
        ApplyLimits(plan, spec);
        db.Plans.Add(plan);
        await SaveUniqueAsync("Nama plan sudah dipakai.", ct);
        return plan;
    }

    public async Task<Plan> UpdatePlanAsync(long id, PlanSpec spec, CancellationToken ct)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw GatewayException.NotFound("Plan");
        if (spec.Name is not null) plan.Name = RequireName(spec.Name, 100, "name");
        ApplyLimits(plan, spec);
        await SaveUniqueAsync("Nama plan sudah dipakai.", ct);
        return plan;
    }

    /// <summary>Plan yang masih dipakai tenant tidak boleh dihapus.</summary>
    public async Task DeletePlanAsync(long id, CancellationToken ct)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw GatewayException.NotFound("Plan");
        if (await db.Tenants.AnyAsync(t => t.PlanId == id, ct))
            throw GatewayException.Conflict("plan_in_use", "Plan masih dipakai tenant dan tidak bisa dihapus.");
        db.Plans.Remove(plan);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 547 })
        {
            // Tenant memakai plan ini di sela pemeriksaan dan penghapusan; foreign key menolak.
            db.ChangeTracker.Clear();
            throw GatewayException.Conflict("plan_in_use", "Plan masih dipakai tenant dan tidak bisa dihapus.");
        }
    }

    private static void ApplyLimits(Plan plan, PlanSpec spec)
    {
        if (spec.MaxProjects is <= 0 || spec.MaxApiKeys is <= 0 || spec.MaxRequestsPerMonth is <= 0 || spec.MaxTokensPerMonth is <= 0)
            throw GatewayException.BadRequest("invalid_limit", "Batas plan harus lebih besar dari 0; kosongkan untuk tanpa batas.");
        plan.MaxProjects = spec.MaxProjects;
        plan.MaxApiKeys = spec.MaxApiKeys;
        plan.MaxRequestsPerMonth = spec.MaxRequestsPerMonth;
        plan.MaxTokensPerMonth = spec.MaxTokensPerMonth;
    }

    // --- tenant --------------------------------------------------------------------------------

    public Task<List<TenantRow>> ListTenantsAsync(CancellationToken ct) =>
        (from t in db.Tenants.AsNoTracking()
         join p in db.Plans on t.PlanId equals p.Id
         orderby t.Name, t.Id
         select new TenantRow(t, p.Name)).Take(500).ToListAsync(ct);

    public async Task<TenantRow> GetTenantAsync(Guid publicId, CancellationToken ct) =>
        await (from t in db.Tenants.AsNoTracking()
               join p in db.Plans on t.PlanId equals p.Id
               where t.PublicId == publicId
               select new TenantRow(t, p.Name)).FirstOrDefaultAsync(ct) ?? throw GatewayException.NotFound("Tenant");

    /// <summary>
    /// Tenant + owner pertamanya dalam satu transaksi: owner yang gagal (mis. email sudah dipakai) tidak
    /// meninggalkan tenant tanpa pemilik.
    /// </summary>
    public async Task<(Tenant Tenant, InvitedUser Owner)> CreateTenantAsync(
        string? name, string? slug, long? planId, string? ownerEmail, string? ownerDisplayName, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = await provisioning.CreateTenantAsync(name ?? "", slug ?? "", planId, ct);
        var owner = await InTenantAsync(tenant.Id, () => users.InviteAsync(ownerEmail ?? "", ownerDisplayName ?? "", Roles.Owner, ct));
        await tx.CommitAsync(ct);
        return (tenant, owner);
    }

    public async Task<Tenant> UpdateTenantAsync(Guid publicId, TenantPatch patch, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.PublicId == publicId, ct) ?? throw GatewayException.NotFound("Tenant");
        if (patch.Name is not null) tenant.Name = RequireName(patch.Name, 200, "name");
        if (patch.Status is not null)
            tenant.Status = patch.Status is TenantStatuses.Active or TenantStatuses.Suspended
                ? patch.Status : throw GatewayException.BadRequest("invalid_status", "status harus active atau suspended.");
        if (patch.PlanId is { } planId)
        {
            if (!await db.Plans.AnyAsync(p => p.Id == planId, ct)) throw GatewayException.NotFound("Plan");
            tenant.PlanId = planId;
        }
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    /// <summary>Undang owner tambahan untuk sebuah tenant (email unik global, seperti undangan lain).</summary>
    public async Task<InvitedUser> InviteOwnerAsync(Guid publicId, string? email, string? displayName, CancellationToken ct)
    {
        var tenantId = await TenantIdAsync(publicId, ct);
        return await InTenantAsync(tenantId, () => users.InviteAsync(email ?? "", displayName ?? "", Roles.Owner, ct));
    }

    /// <summary>Token reset satu kali pakai untuk user tenant; user tenant lain atau platform admin = NotFound.</summary>
    public async Task<string> ResetUserPasswordAsync(Guid publicId, long userId, CancellationToken ct)
    {
        var tenantId = await TenantIdAsync(publicId, ct);
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.TenantId == tenantId, ct)) throw GatewayException.NotFound("Pengguna");
        return await InTenantAsync(tenantId, () => users.IssueResetTokenAsync(userId, ct));
    }

    // --- ringkasan lintas tenant ---------------------------------------------------------------

    /// <summary>Pemakaian semua tenant; kunci baris diganti public_id agar id internal tidak ikut keluar.</summary>
    public async Task<List<UsageRow>> PlatformUsageAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await reports.PlatformOverviewAsync(from, to, ct);
        var ids = rows.Select(r => long.TryParse(r.Key, out var id) ? id : 0).Where(id => id != 0).Distinct().ToList();
        var publicIds = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.PublicId, ct);
        return rows.Select(r => long.TryParse(r.Key, out var id) && publicIds.TryGetValue(id, out var publicId)
            ? r with { Key = publicId.ToString() } : r).ToList();
    }

    /// <summary>
    /// Audit seluruh instalasi (semua tenant dan aksi tingkat platform), <b>metadata saja</b>: tanpa
    /// <c>detail_json</c> sehingga detail milik tenant tidak ikut terbaca platform admin.
    /// </summary>
    public async Task<Page<PlatformAuditRow>> PlatformAuditAsync(DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct)
    {
        if (from is { } f && to is { } t && t < f) throw GatewayException.BadRequest("invalid_range", "'to' tidak boleh sebelum 'from'.");
        pageSize = Math.Clamp(pageSize, 1, UsageReports.MaxPageSize);
        page = Math.Clamp(page, 1, UsageReports.MaxPageNumber); // batas atas: offset tidak boleh overflow int

        var q = db.AuditLogs.AsNoTracking();
        if (from is { } fromValue) q = q.Where(a => a.CreatedAt >= fromValue);
        if (to is { } toValue) q = q.Where(a => a.CreatedAt < toValue);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new PlatformAuditRow(a.Id, a.UserId, a.Action, a.Entity, a.EntityId, a.Ip, a.CreatedAt)).ToListAsync(ct);
        return new Page<PlatformAuditRow>(items, total, page, pageSize);
    }

    // --- katalog master (tulis) ----------------------------------------------------------------

    public async Task<ProviderTemplate> CreateTemplateAsync(TemplateSpec spec, CancellationToken ct)
    {
        var template = new ProviderTemplate
        {
            Code = RequireCode(spec.Code),
            Name = RequireName(spec.Name, 100, "name"),
            DefaultBaseUrl = RequireBaseUrl(spec.DefaultBaseUrl),
        };
        ApplyTemplate(template, spec);
        db.ProviderTemplates.Add(template);
        await SaveUniqueAsync("Kode template sudah dipakai.", ct);
        return template;
    }

    public async Task<ProviderTemplate> UpdateTemplateAsync(long id, TemplateSpec spec, CancellationToken ct)
    {
        var template = await db.ProviderTemplates.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw GatewayException.NotFound("Template provider");
        template.Code = RequireCode(spec.Code);
        template.Name = RequireName(spec.Name, 100, "name");
        template.DefaultBaseUrl = RequireBaseUrl(spec.DefaultBaseUrl);
        ApplyTemplate(template, spec);
        await SaveUniqueAsync("Kode template sudah dipakai.", ct);
        return template;
    }

    /// <summary>Upsert manual: baris (template, upstream_model) yang sudah ada diperbarui dan bersumber <c>manual</c>.</summary>
    public async Task<CatalogModel> UpsertCatalogModelAsync(CatalogModelSpec spec, CancellationToken ct)
    {
        var (templateId, upstreamModel) = await ResolveCatalogTargetAsync(spec, ct);
        var row = await db.CatalogModels.FirstOrDefaultAsync(m => m.TemplateId == templateId && m.UpstreamModel == upstreamModel, ct);
        if (row is null)
        {
            row = new CatalogModel { TemplateId = templateId, UpstreamModel = upstreamModel, DisplayName = "" };
            db.CatalogModels.Add(row);
        }
        ApplyCatalog(row, spec);
        await SaveUniqueAsync("Model katalog sudah ada untuk template ini.", ct);
        return row;
    }

    public async Task<CatalogModel> UpdateCatalogModelAsync(long id, CatalogModelSpec spec, CancellationToken ct)
    {
        var row = await db.CatalogModels.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw GatewayException.NotFound("Model katalog");
        var (templateId, upstreamModel) = await ResolveCatalogTargetAsync(spec, ct);
        row.TemplateId = templateId;
        row.UpstreamModel = upstreamModel;
        ApplyCatalog(row, spec);
        await SaveUniqueAsync("Model katalog sudah ada untuk template ini.", ct);
        return row;
    }

    /// <summary>Sync katalog memakai service key yang tidak pernah disimpan; url kosong = sync_url template bawaan.</summary>
    public async Task<CatalogSyncResult> SyncCatalogAsync(string? serviceKey, string? url, CancellationToken ct)
    {
        var target = url?.Trim();
        if (string.IsNullOrEmpty(target))
            target = await db.ProviderTemplates.AsNoTracking()
                .Where(t => t.SyncKind == SyncKinds.OpenCodeConfig && t.SyncUrl != null)
                .OrderBy(t => t.Id).Select(t => t.SyncUrl!).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(target)) throw GatewayException.BadRequest("no_sync_url", "Tidak ada URL sinkronisasi katalog.");
        // Bentuk dan keamanan URL diperiksa OpenCodeCatalogSync (https + anti-SSRF).
        return await sync.SyncAsync(serviceKey ?? "", target, ct);
    }

    // --- pembantu ------------------------------------------------------------------------------

    private async Task<long> TenantIdAsync(Guid publicId, CancellationToken ct) =>
        await db.Tenants.Where(t => t.PublicId == publicId).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct)
        ?? throw GatewayException.NotFound("Tenant");

    /// <summary>Beberapa operasi memakai service berkonteks tenant; konteks dipulihkan apa pun hasilnya.</summary>
    private async Task<T> InTenantAsync<T>(long tenantId, Func<Task<T>> action)
    {
        var previous = db.CurrentTenantId;
        db.CurrentTenantId = tenantId;
        try { return await action(); }
        finally { db.CurrentTenantId = previous; }
    }

    private async Task<(long TemplateId, string UpstreamModel)> ResolveCatalogTargetAsync(CatalogModelSpec spec, CancellationToken ct)
    {
        var code = RequireCode(spec.TemplateCode);
        var templateId = await db.ProviderTemplates.Where(t => t.Code == code).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct)
            ?? throw GatewayException.NotFound("Template provider");
        var upstream = (spec.UpstreamModel ?? "").Trim();
        if (upstream.Length is 0 or > 200 || upstream.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw GatewayException.BadRequest("invalid_upstream_model", "upstreamModel wajib diisi, maksimal 200 karakter tanpa spasi.");
        return (templateId, upstream);
    }

    private void ApplyTemplate(ProviderTemplate template, TemplateSpec spec)
    {
        var type = spec.Type ?? ProviderTypes.OpenAi;
        if (type != ProviderTypes.OpenAi) throw GatewayException.BadRequest("invalid_type", "type harus openai.");
        template.Type = type;

        var authHeader = spec.AuthHeader ?? "Authorization";
        var authPrefix = spec.AuthPrefix ?? "Bearer ";
        if (!HeaderNameRegex().IsMatch(authHeader)) throw GatewayException.BadRequest("invalid_auth_header", "Nama header auth tidak valid.");
        if (authPrefix.Length > 30 || authPrefix.Any(c => c < ' ' || c > '~'))
            throw GatewayException.BadRequest("invalid_auth_prefix", "Prefix auth tidak valid.");
        template.AuthHeader = authHeader;
        template.AuthPrefix = authPrefix;

        if (string.IsNullOrEmpty(spec.ModelsPath)) template.ModelsPath = null;
        else if (spec.ModelsPath.Length > 200 || !SafePathRegex().IsMatch(spec.ModelsPath))
            throw GatewayException.BadRequest("invalid_models_path", "models_path tidak valid.");
        else template.ModelsPath = spec.ModelsPath;

        if (string.IsNullOrEmpty(spec.SyncKind)) template.SyncKind = null;
        else if (spec.SyncKind != SyncKinds.OpenCodeConfig)
            throw GatewayException.BadRequest("invalid_sync_kind", $"syncKind harus {SyncKinds.OpenCodeConfig} atau kosong.");
        else template.SyncKind = spec.SyncKind;

        if (string.IsNullOrEmpty(spec.SyncUrl)) template.SyncUrl = null;
        else
        {
            // Sinkronisasi selalu lewat kebijakan keluar (https); URL lain tidak akan bisa dipakai.
            outbound.RequireHttpsUri(spec.SyncUrl, "invalid_sync_url");
            template.SyncUrl = spec.SyncUrl;
        }

        template.Enabled = spec.Enabled;
    }

    private static void ApplyCatalog(CatalogModel row, CatalogModelSpec spec)
    {
        row.DisplayName = RequireName(spec.DisplayName, 200, "display_name");

        var family = spec.ApiFamily ?? ApiFamilies.OpenAiChat;
        if (!ApiFamilies.All.Contains(family)) throw GatewayException.BadRequest("invalid_api_family", "apiFamily tidak dikenal.");
        row.ApiFamily = family;

        if (spec.ContextWindow is <= 0 || spec.MaxInputTokens is <= 0 || spec.MaxOutputTokens is <= 0)
            throw GatewayException.BadRequest("invalid_limit", "contextWindow, maxInputTokens, dan maxOutputTokens harus lebih besar dari 0.");
        row.ContextWindow = spec.ContextWindow;
        row.MaxInputTokens = spec.MaxInputTokens;
        row.MaxOutputTokens = spec.MaxOutputTokens;

        if (spec.InputPricePer1M is < 0 || spec.OutputPricePer1M is < 0 || spec.CacheReadPricePer1M is < 0 || spec.CacheWritePricePer1M is < 0)
            throw GatewayException.BadRequest("invalid_price", "Harga tidak boleh negatif.");
        row.InputPricePer1M = spec.InputPricePer1M;
        row.OutputPricePer1M = spec.OutputPricePer1M;
        row.CacheReadPricePer1M = spec.CacheReadPricePer1M;
        row.CacheWritePricePer1M = spec.CacheWritePricePer1M;

        var currency = spec.Currency ?? "USD";
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw GatewayException.BadRequest("invalid_currency", "Mata uang harus kode 3 huruf kapital (mis. USD).");
        row.Currency = currency;

        var tiers = spec.Tiers ?? [];
        if (tiers.Any(t => t.MinInputTokens < 1 || t.Input < 0 || t.Output < 0 || t.CacheRead < 0 || t.CacheWrite < 0))
            throw GatewayException.BadRequest("invalid_price", "Tier harus mulai dari >= 1 token dan harga tidak boleh negatif.");
        if (tiers.GroupBy(t => t.MinInputTokens).Any(g => g.Count() > 1))
            throw GatewayException.BadRequest("invalid_price", "Tier tidak boleh punya ambang yang sama.");
        row.ExtraTiersJson = tiers.Count == 0 ? null : JsonSerializer.Serialize(tiers);

        row.SupportsTools = spec.SupportsTools;
        row.SupportsVision = spec.SupportsVision;
        row.SupportsReasoning = spec.SupportsReasoning;
        row.Source = CatalogSources.Manual;
        row.Enabled = spec.Enabled;
    }

    private async Task SaveUniqueAsync(string conflictMessage, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            throw GatewayException.Conflict("already_exists", conflictMessage);
        }
    }

    /// <summary>Pola <c>code</c> template/katalog: huruf kecil, angka, '.', '_', '-', maksimal 50 karakter.</summary>
    public static bool IsValidCode(string? value) => CodeRegex().IsMatch(value?.Trim() ?? "");

    private static string RequireCode(string? value)
    {
        value = value?.Trim() ?? "";
        if (!IsValidCode(value))
            throw GatewayException.BadRequest("invalid_code", "code harus huruf kecil, angka, '.', '_', atau '-' (maks 50 karakter).");
        return value;
    }

    private static string RequireName(string? value, int max, string field)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 || value.Length > max)
            throw GatewayException.BadRequest("invalid_" + field, $"{field} wajib diisi, maksimal {max} karakter.");
        return value;
    }

    /// <summary>Template adalah resep koneksi provider, jadi URL-nya wajib lolos kebijakan keluar yang sama (https).</summary>
    private string RequireBaseUrl(string? value)
    {
        value = value?.Trim() ?? "";
        outbound.RequireHttpsUri(value, "invalid_base_url");
        return value;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,49}$")] private static partial Regex CodeRegex();
    [GeneratedRegex("^[A-Za-z0-9-]{1,100}$")] private static partial Regex HeaderNameRegex();
    // Path relatif tanpa awalan '/' dan tanpa segmen '..' (tidak boleh naik keluar dari base_url).
    [GeneratedRegex(@"^(?!.*\.\.)[A-Za-z0-9_~-][A-Za-z0-9._~/-]{0,199}$")] private static partial Regex SafePathRegex();
}
