using System.Globalization;
using System.Text.Json;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Proxy;

/// <param name="Denial">Terisi bila permintaan harus ditolak.</param>
/// <param name="MaxTokens">Batas output terkecil dari semua kebijakan; NULL = tidak dibatasi kebijakan.</param>
public sealed record PolicyResult(PolicyDenial? Denial, int? MaxTokens);

/// <param name="Reason">Disimpan di <c>usage_logs.denied_reason</c>: kode + scope penyebab.</param>
public sealed record PolicyDenial(int Status, string Type, string Code, string Message, string Reason, TimeSpan? RetryAfter)
{
    public GatewayResponse ToResponse() =>
        GatewayResponse.Error(Status, Type, Code, Message,
            RetryAfter is { } r ? ((long)Math.Ceiling(r.TotalSeconds)).ToString(CultureInfo.InvariantCulture) : null);
}

/// <summary>
/// Menerapkan kebijakan tenant, project, dan key. Semua scope dievaluasi sendiri-sendiri terhadap pemakaiannya
/// (kuota tenant menghitung seluruh tenant, kuota project hanya project itu, kuota key hanya key itu); permintaan
/// lolos hanya bila lolos di semuanya. Daftar model = irisan semua daftar; batas token = yang terkecil.
/// ponytail: kuota dibaca dari <c>usage_daily</c> sebelum request dikirim, jadi request yang sedang berjalan bisa
/// melewati batas sedikit (soft limit). Untuk batas keras perlu reservasi token sebelum forwarding.
/// </summary>
public sealed class PolicyEngine(GatewayDbContext db, RequestRateLimiter limiter, TimeProvider clock)
{
    private sealed record ScopeUsage(long DayTokens, long MonthTokens, decimal MonthCost, long MonthRequests);

    public async Task<PolicyResult> EvaluateAsync(GatewayCaller caller, string modelAlias, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var untilTomorrow = today.AddDays(1).ToDateTime(TimeOnly.MinValue) - now;
        var untilNextMonth = monthStart.AddMonths(1).ToDateTime(TimeOnly.MinValue) - now;

        var scopes = new (string Name, long Id)[]
        {
            (PolicyScopes.Tenant, caller.TenantId), (PolicyScopes.Project, caller.ProjectId), (PolicyScopes.Key, caller.Key.Id),
        };
        var policies = (await db.Policies.AsNoTracking()
                .Where(p => p.Enabled && ((p.Scope == PolicyScopes.Tenant && p.ScopeId == caller.TenantId)
                                          || (p.Scope == PolicyScopes.Project && p.ScopeId == caller.ProjectId)
                                          || (p.Scope == PolicyScopes.Key && p.ScopeId == caller.Key.Id)))
                .ToListAsync(ct))
            .ToDictionary(p => p.Scope);

        // 1. Daftar model (irisan semua scope yang punya daftar).
        foreach (var (name, _) in scopes)
            if (policies.TryGetValue(name, out var p) && p.AllowedModelsJson is not null && !Allows(p.AllowedModelsJson, modelAlias))
                return Deny(403, ErrorTypes.Permission, "model_not_allowed",
                    $"The model '{modelAlias}' is not allowed for this {name}.", $"model_not_allowed:{name}", null);

        // 2. Batas plan tenant, lalu kuota dan anggaran tiap scope.
        var plan = await (from t in db.Tenants.AsNoTracking() where t.Id == caller.TenantId
                          join pl in db.Plans.AsNoTracking() on t.PlanId equals pl.Id select pl).FirstAsync(ct);
        foreach (var (name, id) in scopes)
        {
            policies.TryGetValue(name, out var p);
            var needsPlan = name == PolicyScopes.Tenant && (plan.MaxRequestsPerMonth is not null || plan.MaxTokensPerMonth is not null);
            var hasQuota = p is not null && (p.DailyTokenQuota is not null || p.MonthlyTokenQuota is not null || p.MonthlyBudget is not null);
            if (!needsPlan && !hasQuota) continue;

            var usage = await UsageAsync(name, id, today, monthStart, ct);
            if (needsPlan)
            {
                if (plan.MaxRequestsPerMonth is { } maxRequests && usage.MonthRequests >= maxRequests)
                    return Deny(429, ErrorTypes.RateLimit, "plan_limit_exceeded", "The monthly request limit of your plan was reached.",
                        "plan_limit_exceeded:requests", untilNextMonth);
                if (plan.MaxTokensPerMonth is { } maxTokens && usage.MonthTokens >= maxTokens)
                    return Deny(429, ErrorTypes.RateLimit, "plan_limit_exceeded", "The monthly token limit of your plan was reached.",
                        "plan_limit_exceeded:tokens", untilNextMonth);
            }
            if (p?.DailyTokenQuota is { } daily && usage.DayTokens >= daily)
                return Deny(429, ErrorTypes.RateLimit, "daily_quota_exceeded", $"The daily token quota of this {name} was reached.",
                    $"daily_quota_exceeded:{name}", untilTomorrow);
            if (p?.MonthlyTokenQuota is { } monthly && usage.MonthTokens >= monthly)
                return Deny(429, ErrorTypes.RateLimit, "monthly_quota_exceeded", $"The monthly token quota of this {name} was reached.",
                    $"monthly_quota_exceeded:{name}", untilNextMonth);
            if (p?.MonthlyBudget is { } budget && usage.MonthCost >= budget)
                return Deny(429, ErrorTypes.RateLimit, "budget_exceeded", $"The monthly budget of this {name} was reached.",
                    $"budget_exceeded:{name}", untilNextMonth);
        }

        // 3. Rate limit paling akhir: hitungan hanya naik bila semua pemeriksaan lain lolos.
        var rateChecks = scopes.Where(s => policies.TryGetValue(s.Name, out var p) && p.RequestsPerMinute is not null)
            .Select(s => (s.Name, s.Id, policies[s.Name].RequestsPerMinute!.Value)).ToList();
        if (rateChecks.Count > 0 && !limiter.TryAcquire(rateChecks, out var retryAfter))
            return Deny(429, ErrorTypes.RateLimit, "rate_limit_exceeded", "Too many requests; slow down.", "rate_limit_exceeded", retryAfter);

        int? maxOut = policies.Values.Where(p => p.MaxTokensPerRequest is not null).Select(p => p.MaxTokensPerRequest).Min();
        return new PolicyResult(null, maxOut);
    }

    private async Task<ScopeUsage> UsageAsync(string scope, long id, DateOnly today, DateOnly monthStart, CancellationToken ct)
    {
        var query = db.UsageDailies.AsNoTracking().Where(u => u.Day >= monthStart);
        if (scope == PolicyScopes.Project) query = query.Where(u => u.ProjectId == id);
        else if (scope == PolicyScopes.Key) query = query.Where(u => u.ApiKeyId == id);

        // Permintaan yang ditolak tidak memakai jatah plan.
        var row = await query.GroupBy(_ => 1).Select(g => new
        {
            Day = g.Where(u => u.Day == today).Sum(u => u.InputTokens + u.OutputTokens),
            Month = g.Sum(u => u.InputTokens + u.OutputTokens),
            Cost = g.Sum(u => u.Cost),
            Requests = g.Sum(u => u.Requests - u.Denied),
        }).FirstOrDefaultAsync(ct);
        return row is null ? new ScopeUsage(0, 0, 0m, 0) : new ScopeUsage(row.Day, row.Month, row.Cost, row.Requests);
    }

    private static bool Allows(string json, string alias)
    {
        try { return (JsonSerializer.Deserialize<string[]>(json) ?? []).Contains(alias, StringComparer.Ordinal); }
        catch (JsonException) { return false; } // daftar rusak: tolak (fail closed)
    }

    private static PolicyResult Deny(int status, string type, string code, string message, string reason, TimeSpan? retryAfter) =>
        new(new PolicyDenial(status, type, code, message, reason, retryAfter), null);
}
