using System.Text.RegularExpressions;
using AiGateway.Core.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiGateway.Core.Data;

public class GatewayDbContext(DbContextOptions<GatewayDbContext> options, TenantSessionInterceptor tenantSession)
    : DbContext(options), IDataProtectionKeyContext
{
    public const long NoTenant = -1;

    /// <summary>
    /// Tenant aktif untuk instance ini. Selama <see cref="NoTenant"/>, query entity milik tenant tidak
    /// mengembalikan apa pun (fail closed); kode platform memakai IgnoreQueryFilters() secara eksplisit.
    /// Nilai diteruskan ke interceptor yang memasang SESSION_CONTEXT untuk RLS SQL Server.
    /// </summary>
    public long CurrentTenantId
    {
        get => tenantSession.TenantId;
        set => tenantSession.TenantId = value;
    }

    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ProviderTemplate> ProviderTemplates => Set<ProviderTemplate>();
    public DbSet<CatalogModel> CatalogModels => Set<CatalogModel>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<ModelRoute> ModelRoutes => Set<ModelRoute>();
    public DbSet<ModelPrice> ModelPrices => Set<ModelPrice>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<UsageLog> UsageLogs => Set<UsageLog>();
    public DbSet<UsageDaily> UsageDailies => Set<UsageDaily>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        string In(string column, params string[] values) =>
            $"[{column}] IN ({string.Join(",", values.Select(v => $"'{v}'"))})";

        b.Entity<Plan>(e =>
        {
            e.ToTable("plans");
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants", t => t.HasCheckConstraint("ck_tenants_status",
                In("status", TenantStatuses.Active, TenantStatuses.Suspended)));
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(63);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users", t =>
            {
                t.HasCheckConstraint("ck_users_role", In("role", Roles.All));
                t.HasCheckConstraint("ck_users_tenant_role",
                    "([role] = 'platform_admin' AND [tenant_id] IS NULL) OR ([role] <> 'platform_admin' AND [tenant_id] IS NOT NULL)");
            });
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.PasswordHash).HasMaxLength(500);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.SecurityStamp).HasMaxLength(40);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.TenantId);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Ip).HasMaxLength(45);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<UserToken>(e =>
        {
            e.ToTable("user_tokens", t => t.HasCheckConstraint("ck_user_tokens_purpose",
                In("purpose", TokenPurposes.Invite, TokenPurposes.ResetPassword)));
            e.Property(x => x.Purpose).HasMaxLength(20);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<ProviderTemplate>(e =>
        {
            e.ToTable("provider_templates", t => t.HasCheckConstraint("ck_provider_templates_type", In("type", ProviderTypes.OpenAi)));
            e.Property(x => x.Code).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Type).HasMaxLength(20);
            e.Property(x => x.DefaultBaseUrl).HasMaxLength(500);
            e.Property(x => x.AuthHeader).HasMaxLength(100);
            e.Property(x => x.AuthPrefix).HasMaxLength(30);
            e.Property(x => x.ModelsPath).HasMaxLength(200);
            e.Property(x => x.SyncKind).HasMaxLength(30);
            e.Property(x => x.SyncUrl).HasMaxLength(500);
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<CatalogModel>(e =>
        {
            e.ToTable("catalog_models", t =>
            {
                t.HasCheckConstraint("ck_catalog_models_source",
                    In("source", CatalogSources.Seed, CatalogSources.Discovered, CatalogSources.Manual));
                t.HasCheckConstraint("ck_catalog_models_prices",
                    "([input_price_per_1m] IS NULL OR [input_price_per_1m] >= 0) AND ([output_price_per_1m] IS NULL OR [output_price_per_1m] >= 0)");
                t.HasCheckConstraint("ck_catalog_models_family", In("api_family", ApiFamilies.All));
            });
            e.Property(x => x.UpstreamModel).HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.InputPricePer1M).HasPrecision(18, 6);
            e.Property(x => x.OutputPricePer1M).HasPrecision(18, 6);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.ApiFamily).HasMaxLength(30);
            e.Property(x => x.CacheReadPricePer1M).HasPrecision(18, 6);
            e.Property(x => x.CacheWritePricePer1M).HasPrecision(18, 6);
            e.HasIndex(x => new { x.TemplateId, x.UpstreamModel }).IsUnique();
            e.HasOne<ProviderTemplate>().WithMany().HasForeignKey(x => x.TemplateId);
        });

        b.Entity<Provider>(e =>
        {
            e.ToTable("providers", t => t.HasCheckConstraint("ck_providers_type", In("type", ProviderTypes.OpenAi)));
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Type).HasMaxLength(20);
            e.Property(x => x.BaseUrl).HasMaxLength(500);
            e.Property(x => x.AuthHeader).HasMaxLength(100);
            e.Property(x => x.AuthPrefix).HasMaxLength(30);
            e.Property(x => x.ModelsPath).HasMaxLength(200);
            e.HasOne<ProviderTemplate>().WithMany().HasForeignKey(x => x.TemplateId);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("[deleted_at] IS NULL");
        });

        b.Entity<ProviderCredential>(e =>
        {
            e.ToTable("provider_credentials", t => t.HasCheckConstraint("ck_provider_credentials_status",
                In("status", CredentialStatuses.Active, CredentialStatuses.Disabled)));
            e.Property(x => x.ApiKeyEncrypted).HasMaxLength(2000);
            e.Property(x => x.KeyHint).HasMaxLength(8);
            e.Property(x => x.Status).HasMaxLength(20);
            // Hanya satu key aktif per provider.
            e.HasIndex(x => x.ProviderId).IsUnique().HasFilter("[status] = 'active'")
                .HasDatabaseName("ux_provider_credentials_active");
            e.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId);
        });

        b.Entity<Model>(e =>
        {
            e.ToTable("models");
            e.Property(x => x.Alias).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Alias }).IsUnique().HasFilter("[deleted_at] IS NULL");
        });

        b.Entity<ModelRoute>(e =>
        {
            e.ToTable("model_routes", t =>
            {
                t.HasCheckConstraint("ck_model_routes_weight", "[weight] >= 1");
                t.HasCheckConstraint("ck_model_routes_priority", "[priority] >= 0");
            });
            e.Property(x => x.UpstreamModel).HasMaxLength(200);
            e.HasIndex(x => new { x.ModelId, x.Priority });
            e.HasOne<Model>().WithMany().HasForeignKey(x => x.ModelId);
            e.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId);
        });

        b.Entity<ModelPrice>(e =>
        {
            e.ToTable("model_prices", t => t.HasCheckConstraint("ck_model_prices_nonneg",
                "[input_price_per_1m] >= 0 AND [output_price_per_1m] >= 0"));
            e.Property(x => x.InputPricePer1M).HasPrecision(18, 6);
            e.Property(x => x.OutputPricePer1M).HasPrecision(18, 6);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.CacheReadPricePer1M).HasPrecision(18, 6);
            e.Property(x => x.CacheWritePricePer1M).HasPrecision(18, 6);
            e.HasIndex(x => new { x.ModelId, x.EffectiveFrom, x.MinInputTokens }).IsUnique();
            e.HasOne<Model>().WithMany().HasForeignKey(x => x.ModelId);
        });

        b.Entity<Project>(e =>
        {
            e.ToTable("projects", t => t.HasCheckConstraint("ck_projects_status",
                In("status", ProjectStatuses.Active, ProjectStatuses.Suspended)));
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(20);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("[deleted_at] IS NULL");
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.KeyPrefix).HasMaxLength(32);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => x.KeyPrefix).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ProjectId });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId);
        });

        b.Entity<Policy>(e =>
        {
            e.ToTable("policies", t => t.HasCheckConstraint("ck_policies_scope",
                In("scope", PolicyScopes.Tenant, PolicyScopes.Project, PolicyScopes.Key)));
            e.Property(x => x.Scope).HasMaxLength(10);
            e.Property(x => x.MonthlyBudget).HasPrecision(18, 6);
            e.HasIndex(x => new { x.TenantId, x.Scope, x.ScopeId }).IsUnique();
        });

        b.Entity<UsageLog>(e =>
        {
            e.ToTable("usage_logs", t => t.HasCheckConstraint("ck_usage_logs_status",
                In("status", UsageStatuses.Ok, UsageStatuses.Denied, UsageStatuses.Error)));
            e.Property(x => x.Status).HasMaxLength(10);
            e.Property(x => x.DeniedReason).HasMaxLength(100);
            e.Property(x => x.FinishReason).HasMaxLength(30);
            e.Property(x => x.InputPriceUsed).HasPrecision(18, 6);
            e.Property(x => x.OutputPriceUsed).HasPrecision(18, 6);
            e.Property(x => x.Cost).HasPrecision(18, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.EndUser).HasMaxLength(200);
            e.Property(x => x.Tags).HasMaxLength(400);
            e.Property(x => x.ClientIp).HasMaxLength(45);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.ProjectId, x.CreatedAt });
        });

        b.Entity<UsageDaily>(e =>
        {
            e.ToTable("usage_daily");
            e.HasKey(x => new { x.TenantId, x.ProjectId, x.ApiKeyId, x.ModelId, x.Day });
            e.Property(x => x.Cost).HasPrecision(18, 8);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.Entity).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.Ip).HasMaxLength(45);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });

        b.Entity<PlatformSetting>(e =>
        {
            e.ToTable("platform_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        b.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        // Isolasi tenant: filter fail-closed + FK ke tenants.
        TenantOwned<Provider>(b, softDelete: true);
        TenantOwned<ProviderCredential>(b);
        TenantOwned<Model>(b, softDelete: true);
        TenantOwned<ModelRoute>(b);
        TenantOwned<ModelPrice>(b);
        TenantOwned<Project>(b, softDelete: true);
        TenantOwned<ApiKey>(b);
        TenantOwned<Policy>(b);
        TenantOwned<UsageLog>(b, foreignKey: false);
        TenantOwned<UsageDaily>(b, foreignKey: false);

        MaintenanceModel.ConfigureMaintenance(b, this);
        ApplyConventions(b);
    }

    private void TenantOwned<T>(ModelBuilder b, bool softDelete = false, bool foreignKey = true)
        where T : class, ITenantOwned
    {
        var e = b.Entity<T>();
        if (softDelete)
            e.HasQueryFilter(x => EF.Property<long>(x, "TenantId") == CurrentTenantId
                                  && EF.Property<DateTime?>(x, "DeletedAt") == null);
        else
            e.HasQueryFilter(x => EF.Property<long>(x, "TenantId") == CurrentTenantId);
        if (foreignKey)
            e.HasOne<Tenant>().WithMany().HasForeignKey("TenantId");
    }

    private static void ApplyConventions(ModelBuilder b)
    {
        const string now = "SYSUTCDATETIME()";
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                p.SetColumnName(Regex.Replace(p.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
                if (p.Name == "CreatedAt") p.SetDefaultValueSql(now);
                if (p.Name == "PublicId") p.SetDefaultValueSql("NEWID()");
            }
            // Cascade di SQL Server menimbulkan banyak jalur ganda; hapus dilakukan eksplisit.
            foreach (var fk in entity.GetForeignKeys()) fk.DeleteBehavior = DeleteBehavior.Restrict;
        }

        b.Entity<ModelPrice>().Property(x => x.InputPricePer1M).HasColumnName("input_price_per_1m");
        b.Entity<ModelPrice>().Property(x => x.OutputPricePer1M).HasColumnName("output_price_per_1m");
        b.Entity<CatalogModel>().Property(x => x.InputPricePer1M).HasColumnName("input_price_per_1m");
        b.Entity<CatalogModel>().Property(x => x.OutputPricePer1M).HasColumnName("output_price_per_1m");
        b.Entity<ModelPrice>().Property(x => x.CacheReadPricePer1M).HasColumnName("cache_read_price_per_1m");
        b.Entity<ModelPrice>().Property(x => x.CacheWritePricePer1M).HasColumnName("cache_write_price_per_1m");
        b.Entity<CatalogModel>().Property(x => x.CacheReadPricePer1M).HasColumnName("cache_read_price_per_1m");
        b.Entity<CatalogModel>().Property(x => x.CacheWritePricePer1M).HasColumnName("cache_write_price_per_1m");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Entity baru memakai tenant konteks bila TenantId belum diisi; menolak penulisan lintas tenant
    /// dan perubahan TenantId pada entity yang sudah ada.
    /// </summary>
    private void EnforceTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            var owned = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                if (owned.TenantId == 0)
                {
                    if (CurrentTenantId == NoTenant)
                        throw new InvalidOperationException(
                            $"{entry.Metadata.ClrType.Name}: TenantId kosong dan konteks tidak punya tenant.");
                    owned.TenantId = CurrentTenantId;
                }
                else if (CurrentTenantId != NoTenant && owned.TenantId != CurrentTenantId)
                {
                    throw new InvalidOperationException(
                        $"{entry.Metadata.ClrType.Name}: penulisan lintas tenant ditolak.");
                }
            }
            else if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name}: TenantId tidak boleh diubah.");
            }
        }
    }
}

public static class GatewayDbContextOptions
{
    public static DbContextOptionsBuilder UseGatewaySqlServer(this DbContextOptionsBuilder o, string connectionString) =>
        o.UseSqlServer(connectionString)
         .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
}
