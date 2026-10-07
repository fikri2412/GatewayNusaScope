using System.Globalization;
using AiGateway.Api.Auth;
using AiGateway.Core.Audit;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Maintenance;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Endpoints;

public sealed record UsageBodyDto(long UsageLogId, string? RequestJson, string? ResponseJson, DateTime ExpiresAt);

public sealed record AlertRuleDto(
    Guid Id, string Name, string Metric, string Scope, Guid? ScopeId, int ThresholdPercent,
    Guid? WebhookId, bool Enabled, DateTime CreatedAt, DateTime? LastTriggeredAt);

public sealed record AlertRuleInput(
    string? Name, string? Metric, string? Scope, Guid? ScopeId, int? ThresholdPercent, Guid? WebhookId, bool? Enabled);

public sealed record AlertRulePatch(
    string? Name, string? Metric, string? Scope, Guid? ScopeId, int? ThresholdPercent, Guid? WebhookId, bool? ClearWebhook, bool? Enabled);

public sealed record AlertEventDto(
    Guid RuleId, string RuleName, string Metric, string Scope, int ThresholdPercent,
    DateOnly PeriodStart, decimal Observed, decimal Limit, DateTime CreatedAt);

public sealed record WebhookDto(Guid Id, string Name, string Url, string SecretHint, bool Enabled, DateTime CreatedAt, DateTime? UpdatedAt);

public sealed record WebhookInput(string? Name, string? Url, string? SigningSecret, bool? Enabled);

public sealed record WebhookPatch(string? Name, string? Url, string? SigningSecret, bool? Enabled);

public sealed record WebhookDeliveryDto(
    long Id, string Status, int Attempts, DateTime NextAttemptAt, int? ResponseStatus, string? LastError, DateTime CreatedAt, DateTime? DeliveredAt);

/// <summary>
/// Isi request (khusus admin tenant, sadar kedaluwarsa), aturan alert, dan webhook. Viewer hanya membaca;
/// semua penulisan lewat <see cref="Policies.TenantAdmin"/>. Isolasi tenant dijaga query filter konteks:
/// id milik tenant lain menghasilkan 404 yang sama dengan id tidak ada.
/// </summary>
public static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenance(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/api/usage/{id:long}/body", GetUsageBodyAsync).RequireAuthorization(Policies.TenantAdmin);

        var alerts = app.MapGroup("/admin/api/alerts");
        alerts.MapGet("", ListAlertsAsync).RequireAuthorization(Policies.TenantViewer);
        alerts.MapGet("events", ListEventsAsync).RequireAuthorization(Policies.TenantViewer);
        alerts.MapPost("", CreateAlertAsync).RequireAuthorization(Policies.TenantAdmin);
        alerts.MapPatch("{id:guid}", UpdateAlertAsync).RequireAuthorization(Policies.TenantAdmin);
        alerts.MapDelete("{id:guid}", DeleteAlertAsync).RequireAuthorization(Policies.TenantAdmin);

        var hooks = app.MapGroup("/admin/api/webhooks");
        hooks.MapGet("", ListWebhooksAsync).RequireAuthorization(Policies.TenantViewer);
        hooks.MapGet("{id:guid}/deliveries", ListDeliveriesAsync).RequireAuthorization(Policies.TenantViewer);
        hooks.MapPost("", CreateWebhookAsync).RequireAuthorization(Policies.TenantAdmin);
        hooks.MapPatch("{id:guid}", UpdateWebhookAsync).RequireAuthorization(Policies.TenantAdmin);
        hooks.MapDelete("{id:guid}", DeleteWebhookAsync).RequireAuthorization(Policies.TenantAdmin);
        return app;
    }

    // --- isi request --------------------------------------------------------------------------

    private static async Task<IResult> GetUsageBodyAsync(
        long id, GatewayDbContext db, AuditWriter audit, TimeProvider clock, CancellationToken ct)
    {
        var body = await db.Set<RequestBody>().AsNoTracking().FirstOrDefaultAsync(b => b.UsageLogId == id, ct);
        if (body is null || body.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
            throw GatewayException.NotFound("Isi request");
        await audit.WriteAsync("usage.body_read", "usage_log", id.ToString(CultureInfo.InvariantCulture));
        return Results.Ok(new UsageBodyDto(id, body.RequestJson, body.ResponseJson, body.ExpiresAt));
    }

    // --- aturan alert -------------------------------------------------------------------------

    private static async Task<IResult> ListAlertsAsync(GatewayDbContext db, CancellationToken ct)
    {
        var rules = await db.Set<AlertRule>().AsNoTracking().OrderBy(r => r.Name).Take(500).ToListAsync(ct);
        var last = await db.Set<AlertEvent>().AsNoTracking()
            .GroupBy(e => e.RuleId).Select(g => new { RuleId = g.Key, Last = g.Max(e => e.CreatedAt) })
            .ToDictionaryAsync(x => x.RuleId, x => x.Last, ct);
        var webhooks = await db.Set<Webhook>().AsNoTracking()
            .Select(w => new { w.Id, w.PublicId }).ToDictionaryAsync(w => w.Id, w => w.PublicId, ct);
        var ruleScopeIds = rules.Select(r => r.ScopeId).Distinct().ToList();
        var projects = await RefMapAsync(db.Projects.AsNoTracking().Where(p => ruleScopeIds.Contains(p.Id)), ct);
        var keys = await RefMapAsync(db.ApiKeys.AsNoTracking().Where(k => ruleScopeIds.Contains(k.Id)), ct);

        return Results.Ok(rules.Select(r => new AlertRuleDto(
            r.PublicId, r.Name, r.Metric, r.Scope,
            r.Scope switch
            {
                PolicyScopes.Project => Ref(projects, r.ScopeId),
                PolicyScopes.Key => Ref(keys, r.ScopeId),
                _ => null,
            },
            r.ThresholdPercent, r.WebhookId is { } wid ? Ref(webhooks, wid) : null, r.Enabled, r.CreatedAt,
            last.TryGetValue(r.Id, out var t) ? t : null)).ToList());
    }

    private static async Task<IResult> ListEventsAsync(int? limit, GatewayDbContext db, CancellationToken ct)
    {
        var events = await db.Set<AlertEvent>().AsNoTracking().OrderByDescending(e => e.Id)
            .Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        var ruleIds = events.Select(e => e.RuleId).Distinct().ToList();
        var rules = await db.Set<AlertRule>().AsNoTracking().Where(r => ruleIds.Contains(r.Id))
            .Select(r => new { r.Id, r.PublicId, r.Name }).ToDictionaryAsync(r => r.Id, ct);
        return Results.Ok(events.Select(e =>
        {
            rules.TryGetValue(e.RuleId, out var r);
            return new AlertEventDto(r?.PublicId ?? Guid.Empty, r?.Name ?? "(dihapus)", e.Metric, e.Scope,
                e.ThresholdPercent, e.PeriodStart, e.ObservedValue, e.LimitValue, e.CreatedAt);
        }).ToList());
    }

    private static async Task<IResult> CreateAlertAsync(
        AlertRuleInput r, GatewayDbContext db, PublicIdResolver ids, CurrentActor actor, AuditWriter audit, CancellationToken ct)
    {
        var name = RequireName(r.Name);
        var metric = RequireMetric(r.Metric);
        var scope = r.Scope ?? PolicyScopes.Tenant;
        var scopeId = await ResolveScopeIdAsync(scope, r.ScopeId, ids, actor, ct);
        var threshold = RequireThreshold(r.ThresholdPercent ?? 80);
        var webhookId = await ResolveWebhookAsync(r.WebhookId, db, ct);

        if (await db.Set<AlertRule>().AnyAsync(x => x.Name == name, ct))
            throw GatewayException.Conflict("already_exists", "Aturan alert dengan nama itu sudah ada.");

        var rule = new AlertRule
        {
            Name = name, Metric = metric, Scope = scope, ScopeId = scopeId,
            ThresholdPercent = threshold, WebhookId = webhookId, Enabled = r.Enabled ?? true,
        };
        db.Set<AlertRule>().Add(rule);
        await SaveUniqueAsync(db, "Aturan alert dengan nama itu sudah ada.", ct);
        await audit.WriteAsync("alert_rule.create", "alert_rule", rule.PublicId.ToString(),
            new { rule.Metric, rule.Scope, rule.ThresholdPercent, HasWebhook = rule.WebhookId is not null });
        return Results.Created($"/admin/api/alerts/{rule.PublicId}",
            await ToDtoAsync(db, rule, r.ScopeId, r.WebhookId, ct));
    }

    private static async Task<IResult> UpdateAlertAsync(
        Guid id, AlertRulePatch r, GatewayDbContext db, PublicIdResolver ids, CurrentActor actor, AuditWriter audit, CancellationToken ct)
    {
        var rule = await db.Set<AlertRule>().FirstOrDefaultAsync(x => x.PublicId == id, ct)
            ?? throw GatewayException.NotFound("Aturan alert");

        if (r.Name is not null)
        {
            var name = RequireName(r.Name);
            if (name != rule.Name && await db.Set<AlertRule>().AnyAsync(x => x.Name == name && x.Id != rule.Id, ct))
                throw GatewayException.Conflict("already_exists", "Aturan alert dengan nama itu sudah ada.");
            rule.Name = name;
        }
        if (r.Metric is not null) rule.Metric = RequireMetric(r.Metric);
        if (r.ThresholdPercent is { } threshold) rule.ThresholdPercent = RequireThreshold(threshold);
        if (r.Enabled is { } enabled) rule.Enabled = enabled;
        if (r.Scope is not null || r.ScopeId is not null)
        {
            rule.Scope = r.Scope ?? rule.Scope;
            rule.ScopeId = await ResolveScopeIdAsync(rule.Scope, r.ScopeId, ids, actor, ct);
        }
        if (r.ClearWebhook == true) rule.WebhookId = null;
        else if (r.WebhookId is not null) rule.WebhookId = await ResolveWebhookAsync(r.WebhookId, db, ct);

        await SaveUniqueAsync(db, "Aturan alert dengan nama itu sudah ada.", ct);
        await audit.WriteAsync("alert_rule.update", "alert_rule", id.ToString(),
            new { rule.Metric, rule.Scope, rule.ThresholdPercent, rule.Enabled });
        return Results.Ok(await ToDtoAsync(db, rule, null, null, ct));
    }

    private static async Task<IResult> DeleteAlertAsync(Guid id, GatewayDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var rule = await db.Set<AlertRule>().FirstOrDefaultAsync(x => x.PublicId == id, ct)
            ?? throw GatewayException.NotFound("Aturan alert");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Set<AlertEvent>().Where(e => e.RuleId == rule.Id).ExecuteDeleteAsync(ct);
        db.Set<AlertRule>().Remove(rule);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await audit.WriteAsync("alert_rule.delete", "alert_rule", id.ToString());
        return Results.NoContent();
    }

    // --- webhook ------------------------------------------------------------------------------

    private static async Task<IResult> ListWebhooksAsync(GatewayDbContext db, CancellationToken ct)
    {
        var webhooks = await db.Set<Webhook>().AsNoTracking().OrderBy(w => w.Name).Take(200).ToListAsync(ct);
        return Results.Ok(webhooks.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateWebhookAsync(
        WebhookInput r, GatewayDbContext db, OutboundSecurityPolicy policy, WebhookSecretProtector protector,
        AuditWriter audit, CancellationToken ct)
    {
        var name = RequireName(r.Name);
        var url = RequireWebhookUrl(policy, r.Url);
        var secret = RequireSecret(r.SigningSecret);
        if (await db.Set<Webhook>().AnyAsync(w => w.Name == name, ct))
            throw GatewayException.Conflict("already_exists", "Webhook dengan nama itu sudah ada.");

        var webhook = new Webhook
        {
            Name = name,
            Url = url,
            SigningSecretEncrypted = protector.Protect(db.CurrentTenantId, secret),
            SecretHint = ProviderKeyProtector.Hint(secret),
            Enabled = r.Enabled ?? true,
        };
        db.Set<Webhook>().Add(webhook);
        await SaveUniqueAsync(db, "Webhook dengan nama itu sudah ada.", ct);
        await audit.WriteAsync("webhook.create", "webhook", webhook.PublicId.ToString(), new { webhook.Name, webhook.Url });
        return Results.Created($"/admin/api/webhooks/{webhook.PublicId}", ToDto(webhook));
    }

    private static async Task<IResult> UpdateWebhookAsync(
        Guid id, WebhookPatch r, GatewayDbContext db, OutboundSecurityPolicy policy, WebhookSecretProtector protector,
        AuditWriter audit, CancellationToken ct)
    {
        var webhook = await db.Set<Webhook>().FirstOrDefaultAsync(w => w.PublicId == id, ct)
            ?? throw GatewayException.NotFound("Webhook");

        if (r.Name is not null)
        {
            var name = RequireName(r.Name);
            if (name != webhook.Name && await db.Set<Webhook>().AnyAsync(w => w.Name == name && w.Id != webhook.Id, ct))
                throw GatewayException.Conflict("already_exists", "Webhook dengan nama itu sudah ada.");
            webhook.Name = name;
        }
        if (r.Url is not null) webhook.Url = RequireWebhookUrl(policy, r.Url);
        if (r.SigningSecret is not null)
        {
            var secret = RequireSecret(r.SigningSecret);
            webhook.SigningSecretEncrypted = protector.Protect(db.CurrentTenantId, secret);
            webhook.SecretHint = ProviderKeyProtector.Hint(secret);
        }
        if (r.Enabled is { } enabled) webhook.Enabled = enabled;
        webhook.UpdatedAt = DateTime.UtcNow;

        await SaveUniqueAsync(db, "Webhook dengan nama itu sudah ada.", ct);
        await audit.WriteAsync("webhook.update", "webhook", id.ToString(), new { webhook.Name, webhook.Url, webhook.Enabled });
        return Results.Ok(ToDto(webhook));
    }

    private static async Task<IResult> DeleteWebhookAsync(Guid id, GatewayDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var webhook = await db.Set<Webhook>().FirstOrDefaultAsync(w => w.PublicId == id, ct)
            ?? throw GatewayException.NotFound("Webhook");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Set<AlertRule>().Where(r => r.WebhookId == webhook.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.WebhookId, (long?)null), ct);
        await db.Set<WebhookDelivery>().Where(d => d.WebhookId == webhook.Id).ExecuteDeleteAsync(ct);
        db.Set<Webhook>().Remove(webhook);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await audit.WriteAsync("webhook.delete", "webhook", id.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> ListDeliveriesAsync(Guid id, int? limit, GatewayDbContext db, CancellationToken ct)
    {
        var webhookId = await db.Set<Webhook>().Where(w => w.PublicId == id).Select(w => (long?)w.Id).FirstOrDefaultAsync(ct)
            ?? throw GatewayException.NotFound("Webhook");
        var deliveries = await db.Set<WebhookDelivery>().AsNoTracking().Where(d => d.WebhookId == webhookId)
            .OrderByDescending(d => d.Id).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        return Results.Ok(deliveries.Select(d => new WebhookDeliveryDto(
            d.Id, d.Status, d.Attempts, d.NextAttemptAt, d.ResponseStatus, d.LastError, d.CreatedAt, d.DeliveredAt)).ToList());
    }

    // --- validasi dan pemetaan ----------------------------------------------------------------

    private static async Task<AlertRuleDto> ToDtoAsync(
        GatewayDbContext db, AlertRule r, Guid? scopeRef, Guid? webhookRef, CancellationToken ct)
    {
        scopeRef ??= r.Scope switch
        {
            PolicyScopes.Project => await db.Projects.AsNoTracking().Where(p => p.Id == r.ScopeId).Select(p => (Guid?)p.PublicId).FirstOrDefaultAsync(ct),
            PolicyScopes.Key => await db.ApiKeys.AsNoTracking().Where(k => k.Id == r.ScopeId).Select(k => (Guid?)k.PublicId).FirstOrDefaultAsync(ct),
            _ => null,
        };
        webhookRef ??= r.WebhookId is { } wid
            ? await db.Set<Webhook>().AsNoTracking().Where(w => w.Id == wid).Select(w => (Guid?)w.PublicId).FirstOrDefaultAsync(ct)
            : null;
        var last = await db.Set<AlertEvent>().AsNoTracking().Where(e => e.RuleId == r.Id)
            .Select(e => (DateTime?)e.CreatedAt).MaxAsync(ct);
        return new AlertRuleDto(r.PublicId, r.Name, r.Metric, r.Scope, scopeRef, r.ThresholdPercent,
            webhookRef, r.Enabled, r.CreatedAt, last);
    }

    private static async Task<Dictionary<long, Guid>> RefMapAsync(IQueryable<Project> projects, CancellationToken ct) =>
        await projects.Select(p => new { p.Id, p.PublicId }).ToDictionaryAsync(p => p.Id, p => p.PublicId, ct);

    private static async Task<Dictionary<long, Guid>> RefMapAsync(IQueryable<ApiKey> keys, CancellationToken ct) =>
        await keys.Select(k => new { k.Id, k.PublicId }).ToDictionaryAsync(k => k.Id, k => k.PublicId, ct);

    private static Guid? Ref(Dictionary<long, Guid> map, long id) => map.TryGetValue(id, out var value) ? value : null;

    private static async Task<long?> ResolveWebhookAsync(Guid? publicId, GatewayDbContext db, CancellationToken ct) =>
        publicId is not { } id
            ? null
            : await db.Set<Webhook>().Where(w => w.PublicId == id).Select(w => (long?)w.Id).FirstOrDefaultAsync(ct)
              ?? throw GatewayException.NotFound("Webhook");

    private static Task<long> ResolveScopeIdAsync(
        string scope, Guid? scopeRef, PublicIdResolver ids, CurrentActor actor, CancellationToken ct) =>
        scope switch
        {
            PolicyScopes.Tenant => Task.FromResult(actor.TenantId
                ?? throw GatewayException.BadRequest("invalid_scope", "Scope tenant memerlukan admin tenant.")),
            PolicyScopes.Project => ids.ProjectAsync(
                scopeRef ?? throw GatewayException.BadRequest("invalid_scope", "scopeId (public id project) wajib untuk scope project."), ct),
            PolicyScopes.Key => ids.ApiKeyAsync(
                scopeRef ?? throw GatewayException.BadRequest("invalid_scope", "scopeId (public id API key) wajib untuk scope key."), ct),
            _ => throw GatewayException.BadRequest("invalid_scope", "scope harus tenant, project, atau key."),
        };

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length is >= 1 and <= 100
            ? trimmed
            : throw GatewayException.BadRequest("invalid_name", "Nama wajib 1-100 karakter.");
    }

    private static string RequireMetric(string? metric)
    {
        var value = metric ?? AlertMetrics.DailyTokens;
        return AlertMetrics.All.Contains(value)
            ? value
            : throw GatewayException.BadRequest("invalid_metric", "metric harus daily_tokens, monthly_tokens, atau monthly_budget.");
    }

    private static int RequireThreshold(int value) => value is >= 1 and <= 100
        ? value
        : throw GatewayException.BadRequest("invalid_threshold", "thresholdPercent harus 1-100.");

    /// <summary>Validasi statis URL keluar yang sama dengan provider/katalog (https, tanpa user/query/fragment, bukan IP privat).</summary>
    private static string RequireWebhookUrl(OutboundSecurityPolicy policy, string? url) =>
        policy.RequireHttpsUri(url ?? "", "invalid_webhook_url").ToString();

    private static string RequireSecret(string? secret) => secret is { Length: >= 16 and <= 200 }
        ? secret
        : throw GatewayException.BadRequest("invalid_signing_secret", "Secret penandatangan wajib 16-200 karakter.");

    private static WebhookDto ToDto(Webhook w) =>
        new(w.PublicId, w.Name, w.Url, w.SecretHint, w.Enabled, w.CreatedAt, w.UpdatedAt);

    private static async Task SaveUniqueAsync(GatewayDbContext db, string conflictMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw GatewayException.Conflict("already_exists", conflictMessage);
        }
    }
}
