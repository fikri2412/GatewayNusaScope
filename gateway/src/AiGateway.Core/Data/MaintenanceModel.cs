using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Data;

/// <summary>
/// Konfigurasi tabel G4 (request_bodies, alert_rules, alert_events, webhooks, webhook_deliveries, job_runs).
/// Dipanggil dari <c>GatewayDbContext.OnModelCreating</c> sebelum konvensi snake_case diterapkan:
/// <c>MaintenanceModel.ConfigureMaintenance(b, this);</c>
/// Query filter memakai <see cref="GatewayDbContext.CurrentTenantId"/> lewat parameter <paramref name="db"/>,
/// supaya EF menggantinya dengan konteks yang sedang dipakai saat query (bukan nilai yang dibekukan saat model dibuat).
/// </summary>
public static class MaintenanceModel
{
    public static ModelBuilder ConfigureMaintenance(this ModelBuilder b, GatewayDbContext db)
    {
        b.Entity<RequestBody>(e =>
        {
            e.ToTable("request_bodies");
            e.HasKey(x => x.UsageLogId);
            e.Property(x => x.UsageLogId).ValueGeneratedNever();
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => new { x.TenantId, x.ExpiresAt });
            Filter<RequestBody>(b, db);
        });

        b.Entity<AlertRule>(e =>
        {
            e.ToTable("alert_rules", t =>
            {
                t.HasCheckConstraint("ck_alert_rules_metric", In("metric", AlertMetrics.All));
                t.HasCheckConstraint("ck_alert_rules_scope", In("scope", PolicyScopes.Tenant, PolicyScopes.Project, PolicyScopes.Key));
                t.HasCheckConstraint("ck_alert_rules_threshold", "[threshold_percent] BETWEEN 1 AND 100");
            });
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Metric).HasMaxLength(30);
            e.Property(x => x.Scope).HasMaxLength(10);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
            Filter<AlertRule>(b, db);
        });

        b.Entity<AlertEvent>(e =>
        {
            e.ToTable("alert_events", t =>
            {
                t.HasCheckConstraint("ck_alert_events_metric", In("metric", AlertMetrics.All));
                t.HasCheckConstraint("ck_alert_events_scope", In("scope", PolicyScopes.Tenant, PolicyScopes.Project, PolicyScopes.Key));
                t.HasCheckConstraint("ck_alert_events_threshold", "[threshold_percent] BETWEEN 1 AND 100");
            });
            e.Property(x => x.Metric).HasMaxLength(30);
            e.Property(x => x.Scope).HasMaxLength(10);
            e.Property(x => x.ObservedValue).HasPrecision(18, 8);
            e.Property(x => x.LimitValue).HasPrecision(18, 8);
            // Dedup per aturan + periode + ambang: satu kejadian per kuota per periode.
            e.HasIndex(x => new { x.RuleId, x.PeriodStart, x.ThresholdPercent }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.PeriodStart });
            Filter<AlertEvent>(b, db);
        });

        b.Entity<Webhook>(e =>
        {
            e.ToTable("webhooks");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.SigningSecretEncrypted).HasMaxLength(2000);
            e.Property(x => x.SecretHint).HasMaxLength(8);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
            Filter<Webhook>(b, db);
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(500);
            // Pekerja pengiriman memilih antrean lintas tenant; indeks diawali status, bukan tenant.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.TenantId, x.WebhookId, x.CreatedAt });
            Filter<WebhookDelivery>(b, db);
        });

        b.Entity<JobRun>(e =>
        {
            e.ToTable("job_runs", t => t.HasCheckConstraint("ck_job_runs_status",
                In("status", JobRunStatuses.Running, JobRunStatuses.Succeeded, JobRunStatuses.Failed)));
            e.Property(x => x.JobName).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Detail).HasMaxLength(400);
            e.HasIndex(x => new { x.JobName, x.StartedAt });
        });

        return b;
    }

    private static void Filter<T>(ModelBuilder b, GatewayDbContext db) where T : class, ITenantOwned =>
        b.Entity<T>().HasQueryFilter(x => EF.Property<long>(x, "TenantId") == db.CurrentTenantId);

    private static string In(string column, params string[] values) =>
        $"[{column}] IN ({string.Join(",", values.Select(v => $"'{v}'"))})";
}
