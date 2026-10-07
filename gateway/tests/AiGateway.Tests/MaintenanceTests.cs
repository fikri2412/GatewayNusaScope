using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Maintenance;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

/// <summary>
/// G4: retensi body/log, alert kuota (dedup periode), webhook bertanda tangan dengan retry, isolasi tenant,
/// dan pencatatan job_runs. Baris body hanya ada bila project menyalakan log_content.
/// </summary>
public class MaintenanceTests(TestDb db) : GatewayTestBase(db)
{
    private static MaintenanceOptions Retention() => new()
    {
        UsageLogRetentionDays = 7,
        RetentionMaxDaysPerRun = 7,
        JobRunRetentionDays = 7,
        DeliveryRetentionDays = 7,
    };

    [Fact]
    public async Task Bodies_are_stored_only_when_log_content_is_on_and_honor_retention()
    {
        var s = await SetupAsync();
        var other = await SetupAsync();

        // Default: log_content = false -> isi tidak pernah disimpan.
        await WithServicesAsync(s.TenantId, async (sp, ctx) =>
        {
            var entry = await RecordUsageAsync(sp, s);
            await sp.GetRequiredService<RequestBodyStore>().RecordAsync(entry, "{\"messages\":[{\"content\":\"rahasia\"}]}", "{\"content\":\"ok\"}");
            Assert.Equal(0, await ctx.Set<RequestBody>().CountAsync());
            return 0;
        });

        await WithTenantAsync(s.TenantId, async (prov, _) =>
        {
            await prov.UpdateProjectAsync(s.ProjectId, new ProjectPatch(LogContent: true, ContentRetentionDays: 2), default);
            return 0;
        });

        var stored = await WithServicesAsync(s.TenantId, async (sp, ctx) =>
        {
            var entry = await RecordUsageAsync(sp, s);
            await sp.GetRequiredService<RequestBodyStore>().RecordAsync(entry, "{\"messages\":[{\"content\":\"rahasia\"}]}", "{\"content\":\"ok\"}");
            return await ctx.Set<RequestBody>().SingleAsync(b => b.UsageLogId == entry.Id);
        });

        Assert.Contains("rahasia", stored.RequestJson!);
        Assert.Contains("ok", stored.ResponseJson!);
        Assert.InRange(stored.ExpiresAt, DateTime.UtcNow.AddDays(2).AddMinutes(-5), DateTime.UtcNow.AddDays(2).AddMinutes(5));

        // Tenant lain tidak melihat body itu (query filter), walau id-nya ditebak.
        await using var otherCtx = Db.NewContext(other.TenantId);
        Assert.Equal(0, await otherCtx.Set<RequestBody>().CountAsync());

        // Tanpa isi sama sekali: tidak ada baris.
        var empty = await WithServicesAsync(s.TenantId, async (sp, ctx) =>
        {
            var entry = await RecordUsageAsync(sp, s);
            await sp.GetRequiredService<RequestBodyStore>().RecordAsync(entry, null, null);
            return await ctx.Set<RequestBody>().CountAsync(b => b.UsageLogId == entry.Id);
        });
        Assert.Equal(0, empty);
    }

    [Fact]
    public async Task Retention_deletes_expired_bodies_and_reconciles_daily_totals_before_purging_logs()
    {
        var s = await SetupAsync();
        var oldDay = DateTime.UtcNow.Date.AddDays(-30);

        long logToday = 0;
        long bodyId = 0;
        await WithServicesAsync(s.TenantId, async (sp, ctx) =>
        {
            var recorder = sp.GetRequiredService<UsageRecorder>();
            await recorder.RecordAsync(NewLog(s, oldDay.AddHours(1), 100, 50, UsageStatuses.Ok));
            await recorder.RecordAsync(NewLog(s, oldDay.AddHours(2), 0, 0, UsageStatuses.Denied));
            var today = NewLog(s, DateTime.UtcNow, 7, 3, UsageStatuses.Ok);
            await recorder.RecordAsync(today);
            logToday = today.Id;

            // Body kedaluwarsa milik tenant ini.
            var body = new RequestBody
            {
                UsageLogId = today.Id, TenantId = s.TenantId,
                RequestJson = "{}", ResponseJson = "{}", ExpiresAt = DateTime.UtcNow.AddDays(-1),
            };
            ctx.Set<RequestBody>().Add(body);
            await ctx.SaveChangesAsync(default);
            bodyId = body.UsageLogId;

            // Rusak agregat hari lama supaya rekonsiliasi terlihat.
            await ctx.UsageDailies.Where(u => u.Day == DateOnly.FromDateTime(oldDay))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.InputTokens, 1L).SetProperty(x => x.Requests, 99L));

            var service = new RetentionService(ctx, Options.Create(Retention()), TimeProvider.System, NullLogger<RetentionService>.Instance);
            var outcome = await service.RunAsync(default);

            Assert.Equal(1, outcome.DaysReconciled);
            Assert.True(outcome.LogsDeleted >= 2, $"logs={outcome.LogsDeleted}");
            Assert.True(outcome.BodiesDeleted >= 1, $"bodies={outcome.BodiesDeleted}");
            return 0;
        });

        await using var check = Db.NewContext(s.TenantId);
        Assert.False(await check.Set<RequestBody>().AnyAsync(b => b.UsageLogId == bodyId));
        Assert.False(await check.UsageLogs.AnyAsync(l => l.CreatedAt < oldDay.AddDays(1)));
        Assert.True(await check.UsageLogs.AnyAsync(l => l.Id == logToday)); // log baru tetap ada

        var day = await check.UsageDailies.AsNoTracking()
            .SingleAsync(u => u.Day == DateOnly.FromDateTime(oldDay) && u.ProjectId == s.ProjectId && u.ApiKeyId == s.KeyId);
        Assert.Equal((2L, 1L, 100L, 50L), (day.Requests, day.Denied, day.InputTokens, day.OutputTokens));
    }

    [Fact]
    public async Task Body_api_requires_tenant_admin_and_hides_other_tenant_or_expired_rows()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        long bodyId;
        long otherBodyId;

        bodyId = await WithServicesAsync(a.TenantId, async (sp, ctx) =>
        {
            await sp.GetRequiredService<ProvisioningService>().UpdateProjectAsync(a.ProjectId, new ProjectPatch(LogContent: true), default);
            var entry = await RecordUsageAsync(sp, a);
            await sp.GetRequiredService<RequestBodyStore>().RecordAsync(entry, "{\"messages\":[{\"content\":\"punya-a\"}]}", "{\"content\":\"ok\"}");
            return await ctx.Set<RequestBody>().Where(x => x.TenantId == a.TenantId).Select(x => x.UsageLogId).SingleAsync();
        });
        otherBodyId = await WithServicesAsync(b.TenantId, async (sp, ctx) =>
        {
            await sp.GetRequiredService<ProvisioningService>().UpdateProjectAsync(b.ProjectId, new ProjectPatch(LogContent: true), default);
            var entry = await RecordUsageAsync(sp, b);
            await sp.GetRequiredService<RequestBodyStore>().RecordAsync(entry, "{\"messages\":[{\"content\":\"punya-b\"}]}", "{\"content\":\"ok\"}");
            return await ctx.Set<RequestBody>().Where(x => x.TenantId == b.TenantId).Select(x => x.UsageLogId).SingleAsync();
        });

        using var adminA = await LoginAsync(a.TenantId, Roles.Admin);
        var own = await adminA.GetAsync($"/admin/api/usage/{bodyId}/body");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var ownRequest = (string?)(await BodyAsync(own))["requestJson"];
        Assert.Contains("punya-a", ownRequest!);

        // Body tenant lain: 404, sama seperti id yang tidak ada.
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/admin/api/usage/{otherBodyId}/body")).StatusCode);

        using var viewerA = await LoginAsync(a.TenantId, Roles.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerA.GetAsync($"/admin/api/usage/{bodyId}/body")).StatusCode);

        // Kedaluwarsa: 404 (row mungkin masih ada sampai retensi berjalan).
        await WithServicesAsync(a.TenantId, async (_, ctx) =>
        {
            await ctx.Set<RequestBody>().Where(x => x.UsageLogId == bodyId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddDays(-1)), default);
            return 0;
        });
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/admin/api/usage/{bodyId}/body")).StatusCode);
    }

    [Fact]
    public async Task Alert_rules_fire_once_per_rule_and_period_and_follow_policy_limits()
    {
        var s = await SetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await WithTenantAsync(s.TenantId, async (prov, ctx) =>
        {
            await prov.SetPolicyAsync(PolicyScopes.Tenant, s.TenantId,
                new PolicySpec(DailyTokenQuota: 100, MonthlyBudget: 5m), default);
            ctx.Set<AlertRule>().AddRange(
                new AlertRule { Name = "harian-80", Metric = AlertMetrics.DailyTokens, ScopeId = s.TenantId, ThresholdPercent = 80 },
                new AlertRule { Name = "harian-100", Metric = AlertMetrics.DailyTokens, ScopeId = s.TenantId, ThresholdPercent = 100 },
                new AlertRule { Name = "budget-80", Metric = AlertMetrics.MonthlyBudget, ScopeId = s.TenantId, ThresholdPercent = 80 },
                // Tanpa kebijakan project: tidak pernah menyala.
                new AlertRule { Name = "project-tanpa-kebijakan", Metric = AlertMetrics.DailyTokens, Scope = PolicyScopes.Project, ScopeId = s.ProjectId, ThresholdPercent = 1 });
            ctx.UsageDailies.Add(new UsageDaily
            {
                TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = s.ModelId,
                Day = today, Requests = 1, InputTokens = 50, Cost = 4.5m,
            });
            await ctx.SaveChangesAsync(default);
            return 0;
        });

        // Token 50/100 (di bawah 80%) + biaya 4.5/5 (di atas 80%) -> hanya budget-80.
        Assert.Equal(1, await EvaluateAsync(s.TenantId));
        Assert.Equal(AlertMetrics.MonthlyBudget, await SingleEventMetricAsync(s.TenantId));

        await SetTokensAsync(s, today, 90);
        Assert.Equal(1, await EvaluateAsync(s.TenantId)); // harian-80
        Assert.Equal(0, await EvaluateAsync(s.TenantId)); // dedup periode + ambang

        await SetTokensAsync(s, today, 120);
        Assert.Equal(1, await EvaluateAsync(s.TenantId)); // harian-100

        await using var ctx2 = Db.NewContext(s.TenantId);
        var events = await ctx2.Set<AlertEvent>().AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.Equal(new[] { "budget-80", "harian-100", "harian-80" }, await RuleNamesAsync(s.TenantId)); // helper mengurutkan abjad
        Assert.All(events, e => Assert.True(e.ObservedValue * 100m >= e.LimitValue * e.ThresholdPercent));
        Assert.Contains(events, e => e.Metric == AlertMetrics.MonthlyBudget && e.PeriodStart == new DateOnly(today.Year, today.Month, 1));
        Assert.DoesNotContain(events, e => e.Metric == AlertMetrics.DailyTokens && e.ThresholdPercent == 1);
    }

    [Fact]
    public async Task Webhook_delivery_signs_payload_retries_with_backoff_and_fails_permanently_on_4xx()
    {
        var s = await SetupAsync();
        const string secret = "signing-secret-1234567890";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var admin = await LoginAsync(s.TenantId, Roles.Admin);
        var created = await admin.PostAsJsonAsync("/admin/api/webhooks",
            new { name = "ops", url = "https://hooks.test/alert", signingSecret = secret });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdJson = await BodyAsync(created);
        var webhookId = (Guid?)createdJson["id"] ?? throw new InvalidOperationException("id webhook hilang.");
        Assert.Equal("7890", (string?)createdJson["secretHint"]);
        Assert.DoesNotContain(secret, createdJson.ToJsonString());

        await WithTenantAsync(s.TenantId, async (prov, ctx) =>
        {
            await prov.SetPolicyAsync(PolicyScopes.Tenant, s.TenantId, new PolicySpec(MonthlyBudget: 5m), default);
            ctx.Set<AlertRule>().Add(new AlertRule
            {
                Name = "budget-webhook", Metric = AlertMetrics.MonthlyBudget, ScopeId = s.TenantId,
                ThresholdPercent = 80, WebhookId = await ctx.Set<Webhook>().Where(w => w.PublicId == webhookId).Select(w => w.Id).SingleAsync(),
            });
            ctx.UsageDailies.Add(new UsageDaily
            {
                TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = s.ModelId,
                Day = today, Requests = 1, Cost = 5m,
            });
            await ctx.SaveChangesAsync(default);
            return 0;
        });

        await WithServicesAsync(s.TenantId, (sp, _) => sp.GetRequiredService<AlertService>().EvaluateTenantAsync(s.TenantId, default));
        var deliveryId = await WithServicesAsync(s.TenantId, (_, ctx) =>
            ctx.Set<WebhookDelivery>().Select(d => d.Id).SingleAsync());

        // Percobaan 1: penerima menjawab 500 -> dicoba lagi dengan tanda tangan yang benar.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = DeliveryService(s.TenantId, handler, maxAttempts: 5);
        var first = await service.RunAsync(default);
        Assert.Equal((0, 1, 0), (first.Sent, first.Retried, first.Failed));

        var payload = Assert.Single(handler.Requests).Body!;
        Assert.Equal("sha256=" + WebhookDeliveryService.Signature(secret, payload), Assert.Single(handler.Requests).Signature);
        Assert.DoesNotContain("rahasia", payload);
        Assert.DoesNotContain("input_tokens", payload);

        await using (var ctx = Db.NewContext(s.TenantId))
        {
            var delivery = await ctx.Set<WebhookDelivery>().AsNoTracking().SingleAsync(d => d.Id == deliveryId);
            Assert.Equal((DeliveryStatuses.Pending, 1), (delivery.Status, delivery.Attempts));
            Assert.Contains("HTTP 500", delivery.LastError!);
            Assert.True(delivery.NextAttemptAt > DateTime.UtcNow, "backoff harus menunda percobaan berikutnya");
        }

        // Percobaan 2: sukses -> terkirim permanen.
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        handler.Requests.Clear();
        await WithServicesAsync(s.TenantId, (_, ctx) =>
            ctx.Set<WebhookDelivery>().Where(d => d.Id == deliveryId)
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)), default));
        var second = await service.RunAsync(default);
        Assert.Equal((1, 0, 0), (second.Sent, second.Retried, second.Failed));
        await using (var ctx = Db.NewContext(s.TenantId))
        {
            var delivery = await ctx.Set<WebhookDelivery>().AsNoTracking().SingleAsync(d => d.Id == deliveryId);
            Assert.Equal(DeliveryStatuses.Succeeded, delivery.Status);
            Assert.NotNull(delivery.DeliveredAt);
            Assert.Equal(200, delivery.ResponseStatus);
        }

        // 400: tidak dicoba lagi, langsung failed.
        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
            {
                TenantId = s.TenantId,
                WebhookId = await ctx.Set<Webhook>().Select(w => w.Id).SingleAsync(),
                Status = DeliveryStatuses.Pending, NextAttemptAt = DateTime.UtcNow.AddSeconds(-1), PayloadJson = "{}",
            });
            await ctx.SaveChangesAsync(default);
            return 0;
        });
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);
        var third = await service.RunAsync(default);
        Assert.Equal((0, 0, 1), (third.Sent, third.Retried, third.Failed));
        await using (var ctx = Db.NewContext(s.TenantId))
            Assert.Equal(DeliveryStatuses.Failed, (await ctx.Set<WebhookDelivery>().AsNoTracking().OrderByDescending(d => d.Id).FirstAsync()).Status);
    }

    [Fact]
    public async Task Alert_and_webhook_api_isolates_tenants_and_never_returns_secrets()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();

        using var adminA = await LoginAsync(a.TenantId, Roles.Admin);
        var webhookB = await adminA.PostAsJsonAsync("/admin/api/webhooks",
            new { name = "tim-a", url = "https://hooks.test/a", signingSecret = "rahasia-tim-a-123456" });
        Assert.Equal(HttpStatusCode.Created, webhookB.StatusCode);
        var webhookId = (Guid?)(await BodyAsync(webhookB))["id"] ?? throw new InvalidOperationException("id webhook hilang.");

        var rule = await adminA.PostAsJsonAsync("/admin/api/alerts",
            new { name = "kuota-a", metric = "daily_tokens", scope = "tenant", thresholdPercent = 80, webhookId });
        Assert.Equal(HttpStatusCode.Created, rule.StatusCode);
        var ruleId = (Guid?)(await BodyAsync(rule))["id"] ?? throw new InvalidOperationException("id aturan hilang.");

        using var adminB = await LoginAsync(b.TenantId, Roles.Admin);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PatchAsJsonAsync($"/admin/api/webhooks/{webhookId}", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/admin/api/webhooks/{webhookId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PatchAsJsonAsync($"/admin/api/alerts/{ruleId}", new { thresholdPercent = 90 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/admin/api/alerts/{ruleId}")).StatusCode);

        var listB = (JsonArray)(await BodyAsync(await adminB.GetAsync("/admin/api/webhooks")))!;
        Assert.Empty(listB);
        Assert.Empty((JsonArray)(await BodyAsync(await adminB.GetAsync("/admin/api/alerts")))!);

        // Viewer boleh membaca, tidak boleh menulis.
        using var viewerA = await LoginAsync(a.TenantId, Roles.Viewer);
        var ownList = await viewerA.GetAsync("/admin/api/webhooks");
        Assert.Equal(HttpStatusCode.OK, ownList.StatusCode);
        Assert.Single((JsonArray)(await BodyAsync(ownList))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerA.PostAsJsonAsync("/admin/api/webhooks",
            new { name = "nope", url = "https://hooks.test/x", signingSecret = "cukup-panjang-1234" })).StatusCode);

        // Aturan yang memakai webhook boleh dihapus di tenant yang benar; webhook tidak bocor.
        Assert.Equal(HttpStatusCode.NoContent, (await adminA.DeleteAsync($"/admin/api/webhooks/{webhookId}")).StatusCode);
        await using var ctx = Db.NewContext(a.TenantId);
        var detached = await ctx.Set<AlertRule>().AsNoTracking().SingleAsync(r => r.PublicId == ruleId);
        Assert.Null(detached.WebhookId);
    }

    [Fact]
    public async Task Maintenance_worker_records_every_job_in_job_runs()
    {
        var maintenance = Factory.Services.GetRequiredService<MaintenanceWorker>();
        await maintenance.RunOnceAsync(default);

        await using var ctx = Db.NewContext();
        var runs = await ctx.Set<JobRun>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(
            new[] { "maintenance.retention", "maintenance.alerts", "maintenance.webhook_delivery" },
            runs.Select(r => r.JobName));
        Assert.All(runs, r =>
        {
            Assert.Equal(JobRunStatuses.Succeeded, r.Status);
            Assert.NotNull(r.FinishedAt);
            Assert.NotNull(r.Detail);
        });
    }

    // --- helpers ------------------------------------------------------------------------------

    private static UsageLog NewLog(Scenario s, DateTime createdAt, int input, int output, string status) => new()
    {
        TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = s.ModelId,
        Status = status, HttpStatus = 200, InputTokens = input, OutputTokens = output, CreatedAt = createdAt,
    };

    private async Task<UsageLog> RecordUsageAsync(IServiceProvider sp, Scenario s)
    {
        var entry = NewLog(s, DateTime.UtcNow, 10, 5, UsageStatuses.Ok);
        await sp.GetRequiredService<UsageRecorder>().RecordAsync(entry);
        return entry;
    }

    private async Task<HttpClient> LoginAsync(long tenantId, string role)
    {
        var (_, _, email) = await NewUserAsync(role, tenantId);
        var response = await Client.PostAsJsonAsync("/admin/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (string?)(await BodyAsync(response))["accessToken"];
        Assert.NotNull(token);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<int> EvaluateAsync(long tenantId) =>
        WithServicesAsync(tenantId, (sp, _) => sp.GetRequiredService<AlertService>().EvaluateTenantAsync(tenantId, default));

    private async Task<string?> SingleEventMetricAsync(long tenantId)
    {
        await using var ctx = Db.NewContext(tenantId);
        return await ctx.Set<AlertEvent>().AsNoTracking().Select(e => e.Metric).SingleAsync();
    }

    private Task SetTokensAsync(Scenario s, DateOnly day, int tokens) =>
        WithServicesAsync(s.TenantId, (_, ctx) => ctx.UsageDailies
            .Where(u => u.Day == day && u.ProjectId == s.ProjectId).ExecuteUpdateAsync(x => x.SetProperty(u => u.InputTokens, (long)tokens), default));

    private async Task<List<string>> RuleNamesAsync(long tenantId)
    {
        await using var ctx = Db.NewContext(tenantId);
        var events = await ctx.Set<AlertEvent>().AsNoTracking().OrderBy(e => e.Id).Select(e => e.RuleId).ToListAsync();
        var rules = await ctx.Set<AlertRule>().AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        return events.Select(id => rules[id]).Order().ToList();
    }

    private WebhookDeliveryService DeliveryService(long tenantId, HttpMessageHandler handler, int maxAttempts)
    {
        var ctx = Db.NewContext(tenantId);
        return new WebhookDeliveryService(
            ctx, new StubHttpFactory(handler), new WebhookSecretProtector(Factory.Services.GetRequiredService<IDataProtectionProvider>()),
            Options.Create(new MaintenanceOptions { WebhookMaxAttempts = maxAttempts, WebhookBackoffBaseSeconds = 1, WebhookBackoffMaxSeconds = 10 }),
            TimeProvider.System, NullLogger<WebhookDeliveryService>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public record Captured(string? Signature, string? Body);

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var signature = request.Headers.TryGetValues("X-Gateway-Signature", out var values) ? values.First() : null;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Captured(signature, body));
            return Respond(request);
        }
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
