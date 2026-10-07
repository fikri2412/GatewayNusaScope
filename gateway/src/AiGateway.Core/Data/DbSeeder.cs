using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Data;

/// <summary>Data awal idempoten: plan Default (tanpa batas) dan platform admin pertama.</summary>
public sealed class DbSeeder(
    GatewayDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<SeedOptions> seed,
    ILogger<DbSeeder> log)
{
    public const string DefaultPlanName = "Default";
    public const string OpenCodeTemplateCode = "opencode";
    public const string OpenCodeDefaultModel = "deepseek-v4.1-flash";
    public const string OpenCodeConfigUrl = "https://opencode.ai/console/api/v2/config";

    public async Task RunAsync(CancellationToken ct)
    {
        if (!await db.Plans.AnyAsync(p => p.Name == DefaultPlanName, ct))
        {
            db.Plans.Add(new Plan { Name = DefaultPlanName });
            await SaveIgnoringDuplicateAsync($"plan {DefaultPlanName}", ct);
        }

        await SeedCatalogAsync(ct);
        await SeedAdminAsync(ct);
    }

    /// <summary>
    /// Simpan data awal; pelanggaran indeks unik berarti barisnya sudah ada (start kedua yang berbarengan,
    /// atau email/nama yang sudah dipakai baris lain), bukan kegagalan start. <c>false</c> = tidak tersimpan.
    /// </summary>
    private async Task<bool> SaveIgnoringDuplicateAsync(string what, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            log.LogInformation("Data awal {What} sudah ada; tidak ditimpa.", what);
            return false;
        }
    }

    /// <summary>
    /// Master data bawaan, hanya menambah yang belum ada (tidak menimpa perubahan admin). Hanya model yang
    /// sudah terbukti jalan yang di-seed; harga/konteks belum diketahui jadi NULL. Tambahan lewat admin atau discover.
    /// </summary>
    private async Task SeedCatalogAsync(CancellationToken ct)
    {
        var template = await db.ProviderTemplates.FirstOrDefaultAsync(t => t.Code == OpenCodeTemplateCode, ct);
        if (template is null)
        {
            template = new ProviderTemplate
            {
                Code = OpenCodeTemplateCode,
                Name = "OpenCode",
                DefaultBaseUrl = "https://opencode.ai/inference/openai/v1/",
                SyncKind = SyncKinds.OpenCodeConfig,
                SyncUrl = OpenCodeConfigUrl,
            };
            db.ProviderTemplates.Add(template);
            if (!await SaveIgnoringDuplicateAsync($"template {OpenCodeTemplateCode}", ct))
                template = await db.ProviderTemplates.FirstAsync(t => t.Code == OpenCodeTemplateCode, ct);
        }

        if (!await db.CatalogModels.AnyAsync(m => m.TemplateId == template.Id && m.UpstreamModel == OpenCodeDefaultModel, ct))
        {
            db.CatalogModels.Add(new CatalogModel
            {
                TemplateId = template.Id,
                UpstreamModel = OpenCodeDefaultModel,
                DisplayName = "DeepSeek V4.1 Flash",
                SupportsReasoning = true,
                Source = CatalogSources.Seed,
            });
            await SaveIgnoringDuplicateAsync($"model katalog {OpenCodeDefaultModel}", ct);
        }
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Role == Roles.PlatformAdmin, ct)) return;

        var s = seed.Value;
        if (string.IsNullOrWhiteSpace(s.AdminEmail) || s.AdminPassword is null)
        {
            log.LogWarning("Belum ada platform admin dan Seed:AdminEmail/AdminPassword belum diisi; tidak ada yang bisa login.");
            return;
        }

        var user = new User
        {
            Email = s.AdminEmail.Trim().ToLowerInvariant(),
            DisplayName = "Platform Admin",
            Role = Roles.PlatformAdmin,
            PasswordHash = "",
        };
        user.PasswordHash = hasher.HashPassword(user, s.AdminPassword);
        db.Users.Add(user);
        if (await SaveIgnoringDuplicateAsync("platform admin", ct))
            log.LogInformation("Platform admin dibuat: {Email}", user.Email);
        else
            log.LogWarning("Platform admin {Email} tidak dibuat: email sudah dipakai user lain atau dibuat proses lain.", user.Email);
    }
}
