using System.Text.Json;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiGateway.Core.Maintenance;

/// <summary>Payload alert untuk webhook: nilai kuota saja, tanpa isi prompt maupun secret provider/key.</summary>
public static class WebhookPayload
{
    public static string Build(AlertRule rule, AlertEvent ev) => JsonSerializer.Serialize(new
    {
        @event = "quota_threshold",
        ruleId = rule.PublicId,
        ruleName = rule.Name,
        metric = ev.Metric,
        scope = ev.Scope,
        thresholdPercent = ev.ThresholdPercent,
        periodStart = ev.PeriodStart,
        observed = ev.ObservedValue,
        limit = ev.LimitValue,
        triggeredAt = ev.CreatedAt,
    });
}

/// <summary>
/// Evaluasi aturan alert kuota terhadap <c>usage_daily</c>. Satu kejadian per (aturan, periode, ambang);
/// evaluasi berulang dalam periode yang sama tidak membuat kejadian baru. Kejadian yang punya webhook
/// langsung masuk antrean <see cref="WebhookDelivery"/> dalam transaksi yang sama (durable, dikirim
/// pekerja terpisah); kegagalan satu aturan tidak membatalkan aturan lain di siklus itu.
/// </summary>
public sealed class AlertService(GatewayDbContext db, TimeProvider clock, ILogger<AlertService> log)
{
    public async Task<int> EvaluateAllAsync(CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatuses.Active).Select(t => t.Id).OrderBy(id => id).ToListAsync(ct);
        var created = 0;
        foreach (var tenantId in tenants)
        {
            created += await EvaluateTenantAsync(tenantId, ct);
            db.ChangeTracker.Clear();
        }
        return created;
    }

    /// <summary>Evaluasi semua aturan tenant; aman dijalankan berkali-kali (dedup periode + ambang).</summary>
    public async Task<int> EvaluateTenantAsync(long tenantId, CancellationToken ct)
    {
        db.CurrentTenantId = tenantId;
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var monthStart = new DateOnly(now.Year, now.Month, 1);

        var rules = await db.Set<AlertRule>().Where(r => r.Enabled).ToListAsync(ct);
        var created = 0;
        foreach (var rule in rules)
        {
            try
            {
                if (await FireAsync(rule, tenantId, today, monthStart, now, ct)) created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Satu aturan yang gagal tidak membatalkan aturan (dan tenant) lain dalam siklus yang sama.
                db.ChangeTracker.Clear();
                log.LogError(ex, "Evaluasi aturan alert {Rule} tenant {TenantId} gagal", rule.Name, tenantId);
            }
        }
        return created;
    }

    /// <summary>
    /// Satu aturan: tulis kejadian ambang beserta antrean webhook-nya dalam satu transaksi, supaya kejadian
    /// tidak pernah tertinggal tanpa pengiriman (dedup periode akan melewatinya selamanya).
    /// </summary>
    private async Task<bool> FireAsync(
        AlertRule rule, long tenantId, DateOnly today, DateOnly monthStart, DateTime now, CancellationToken ct)
    {
        var (periodStart, limit) = await LimitAsync(rule, today, monthStart, ct);
        if (limit is not { } limitValue) return false; // tidak ada kuota yang dipantau

        var observed = rule.Metric switch
        {
            AlertMetrics.DailyTokens => await TokensAsync(rule, today, today, ct),
            AlertMetrics.MonthlyTokens => await TokensAsync(rule, monthStart, today, ct),
            AlertMetrics.MonthlyBudget => await CostAsync(rule, monthStart, ct),
            _ => 0m,
        };
        if (observed * 100m < limitValue * rule.ThresholdPercent) return false;

        if (await db.Set<AlertEvent>().AnyAsync(e => e.RuleId == rule.Id && e.PeriodStart == periodStart
                && e.ThresholdPercent == rule.ThresholdPercent, ct))
            return false;

        var ev = new AlertEvent
        {
            TenantId = tenantId,
            RuleId = rule.Id,
            Metric = rule.Metric,
            Scope = rule.Scope,
            ScopeId = rule.ScopeId,
            ThresholdPercent = rule.ThresholdPercent,
            PeriodStart = periodStart,
            ObservedValue = observed,
            LimitValue = limitValue,
        };
        db.Set<AlertEvent>().Add(ev);

        // Dua SaveChanges dalam satu transaksi (bukan satu SaveChanges): AlertEventId kolom biasa tanpa relasi
        // EF, jadi id kejadian baru diketahui setelah insert. Rollback menjaga keduanya tetap bersama.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct); // ev.Id terisi
            var delivery = await BuildDeliveryAsync(rule, ev, now, ct);
            if (delivery is not null)
            {
                delivery.AlertEventId = ev.Id;
                db.Set<WebhookDelivery>().Add(delivery);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            if (delivery is not null)
                log.LogInformation("Alert {Rule} tenant {TenantId} melewati ambang {Threshold}%; antre webhook {WebhookId}",
                    rule.Name, ev.TenantId, ev.ThresholdPercent, delivery.WebhookId);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await tx.RollbackAsync(ct); // balapan dengan evaluasi lain: kejadian sudah ada
            db.Entry(ev).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Kuota dari kebijakan scope aturan; NULL bila kebijakan tidak ada, nonaktif, atau kuotanya kosong.</summary>
    private async Task<(DateOnly PeriodStart, decimal? Limit)> LimitAsync(
        AlertRule rule, DateOnly today, DateOnly monthStart, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Scope == rule.Scope && p.ScopeId == rule.ScopeId, ct);
        if (policy is null || !policy.Enabled) return (today, (decimal?)null);
        return rule.Metric switch
        {
            AlertMetrics.DailyTokens => (today, (decimal?)policy.DailyTokenQuota),
            AlertMetrics.MonthlyTokens => (monthStart, (decimal?)policy.MonthlyTokenQuota),
            AlertMetrics.MonthlyBudget => (monthStart, (decimal?)policy.MonthlyBudget),
            _ => (today, (decimal?)null),
        };
    }

    private IQueryable<UsageDaily> Scoped(AlertRule rule)
    {
        var q = db.UsageDailies.AsNoTracking();
        if (rule.Scope == PolicyScopes.Project) q = q.Where(u => u.ProjectId == rule.ScopeId);
        else if (rule.Scope == PolicyScopes.Key) q = q.Where(u => u.ApiKeyId == rule.ScopeId);
        return q; // scope tenant: seluruh tenant (query filter)
    }

    private async Task<decimal> TokensAsync(AlertRule rule, DateOnly from, DateOnly to, CancellationToken ct) =>
        await Scoped(rule).Where(u => u.Day >= from && u.Day <= to)
            .Select(u => (long?)(u.InputTokens + u.OutputTokens)).SumAsync(ct) ?? 0;

    private async Task<decimal> CostAsync(AlertRule rule, DateOnly from, CancellationToken ct) =>
        await Scoped(rule).Where(u => u.Day >= from).Select(u => (decimal?)u.Cost).SumAsync(ct) ?? 0m;

    /// <summary>Antrean pengiriman untuk webhook aturan; NULL bila aturan tanpa webhook atau webhooknya nonaktif.</summary>
    private async Task<WebhookDelivery?> BuildDeliveryAsync(AlertRule rule, AlertEvent ev, DateTime now, CancellationToken ct)
    {
        if (rule.WebhookId is not { } webhookId) return null;
        var webhook = await db.Set<Webhook>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == webhookId, ct);
        if (webhook is null || !webhook.Enabled) return null;
        return new WebhookDelivery
        {
            TenantId = ev.TenantId,
            WebhookId = webhook.Id,
            Status = DeliveryStatuses.Pending,
            NextAttemptAt = now,
            PayloadJson = WebhookPayload.Build(rule, ev),
        };
    }
}
