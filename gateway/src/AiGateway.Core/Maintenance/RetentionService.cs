using System.Data;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Maintenance;

public sealed record RetentionOutcome(
    int BodiesDeleted, int DaysReconciled, int LogsDeleted, int DeliveriesDeleted, int JobRunsDeleted,
    int RefreshTokensDeleted, int UserTokensDeleted);

/// <summary>
/// Retensi G4. Sebelum usage_logs satu hari dihapus, hari itu direkonsiliasi ulang dari log mentah ke
/// <c>usage_daily</c> dengan nilai absolut (bukan menambah), jadi total historis tetap utuh walau
/// pencatatan inline sempat gagal. Rekonsiliasi hanya menyentuh hari yang sudah utuh (sebelum hari cutoff),
/// lalu log hari itu dihapus per batch — aman diulang bila proses berhenti di tengah. Refresh/user token
/// yang sudah kedaluwarsa lebih dari sepekan juga dipurge di sini.
/// </summary>
public sealed class RetentionService(
    GatewayDbContext db, IOptions<MaintenanceOptions> options, TimeProvider clock, ILogger<RetentionService> log)
{
    /// <summary>Token kedaluwarsa disimpan sepekan setelah masa berlakunya berakhir (untuk audit singkat), lalu dibuang.</summary>
    private const int ExpiredTokenRetentionDays = 7;

    private const string ReconcileSql = """
        MERGE usage_daily WITH (HOLDLOCK) AS t
        USING (
            SELECT tenant_id, project_id, api_key_id, COALESCE(model_id, 0) AS model_id,
                   COUNT_BIG(*) AS requests,
                   COUNT_BIG(CASE WHEN status = 'denied' THEN 1 END) AS denied,
                   COUNT_BIG(CASE WHEN status = 'error' THEN 1 END) AS errors,
                   SUM(CAST(input_tokens AS bigint)) AS input_tokens,
                   SUM(CAST(output_tokens AS bigint)) AS output_tokens,
                   SUM(cost) AS cost
            FROM usage_logs
            WHERE created_at >= @from AND created_at < @to
            GROUP BY tenant_id, project_id, api_key_id, COALESCE(model_id, 0)
        ) AS s
        ON t.tenant_id = s.tenant_id AND t.project_id = s.project_id AND t.api_key_id = s.api_key_id
           AND t.model_id = s.model_id AND t.day = @day
        WHEN MATCHED THEN UPDATE SET requests = s.requests, denied = s.denied, errors = s.errors,
             input_tokens = s.input_tokens, output_tokens = s.output_tokens, cost = s.cost
        WHEN NOT MATCHED THEN INSERT (tenant_id, project_id, api_key_id, model_id, day, requests, denied, errors, input_tokens, output_tokens, cost)
             VALUES (s.tenant_id, s.project_id, s.api_key_id, s.model_id, @day, s.requests, s.denied, s.errors, s.input_tokens, s.output_tokens, s.cost);
        """;

    public async Task<RetentionOutcome> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        var bodies = await DeleteBodiesAsync(now, o.RetentionBatchSize, ct);

        // usage_logs: hanya hari yang sudah utuh (day < hari cutoff) yang direkonsiliasi lalu dihapus.
        var cutoff = DateOnly.FromDateTime(now).AddDays(-o.UsageLogRetentionDays).ToDateTime(TimeOnly.MinValue);
        var days = await db.UsageLogs.IgnoreQueryFilters()
            .Where(l => l.CreatedAt < cutoff)
            .Select(l => l.CreatedAt.Date).Distinct().OrderBy(d => d).Take(o.RetentionMaxDaysPerRun).ToListAsync(ct);

        var logs = 0;
        var reconciled = 0;
        foreach (var day in days)
        {
            await ReconcileDayAsync(DateOnly.FromDateTime(day), ct);
            reconciled++;
            logs += await DeleteUsageLogsAsync(day.Date, day.Date.AddDays(1), o.RetentionBatchSize, ct);
        }

        var deliveries = await db.Set<WebhookDelivery>().IgnoreQueryFilters()
            .Where(d => d.Status != DeliveryStatuses.Pending && d.CreatedAt < now.AddDays(-o.DeliveryRetentionDays))
            .ExecuteDeleteAsync(ct);
        var jobRuns = await db.Set<JobRun>()
            .Where(j => j.StartedAt < now.AddDays(-o.JobRunRetentionDays)).ExecuteDeleteAsync(ct);

        // Token kedaluwarsa: refresh_tokens/user_tokens di luar RLS, dibuang setelah masa berlakunya lewat sepekan.
        var tokenCutoff = now.AddDays(-ExpiredTokenRetentionDays);
        var refreshTokens = await db.RefreshTokens.Where(t => t.ExpiresAt < tokenCutoff).ExecuteDeleteAsync(ct);
        var userTokens = await db.UserTokens.Where(t => t.ExpiresAt < tokenCutoff).ExecuteDeleteAsync(ct);

        if (bodies + logs + deliveries + jobRuns + refreshTokens + userTokens > 0 || reconciled > 0)
            log.LogInformation(
                "Retensi: bodies={Bodies} hari={Days} logs={Logs} deliveries={Deliveries} jobRuns={JobRuns} refreshTokens={RefreshTokens} userTokens={UserTokens}",
                bodies, reconciled, logs, deliveries, jobRuns, refreshTokens, userTokens);
        return new RetentionOutcome(bodies, reconciled, logs, deliveries, jobRuns, refreshTokens, userTokens);
    }

    private async Task<int> DeleteBodiesAsync(DateTime now, int batch, CancellationToken ct)
    {
        var deleted = 0;
        while (true)
        {
            var n = await db.Database.ExecuteSqlRawAsync(
                "DELETE TOP (@batch) FROM request_bodies WHERE expires_at <= @now",
                new object[] { new SqlParameter("@batch", batch), new SqlParameter("@now", now) }, ct);
            deleted += n;
            if (n < batch) return deleted;
        }
    }

    private async Task<int> DeleteUsageLogsAsync(DateTime from, DateTime to, int batch, CancellationToken ct)
    {
        var deleted = 0;
        while (true)
        {
            var n = await db.Database.ExecuteSqlRawAsync(
                "DELETE TOP (@batch) FROM usage_logs WHERE created_at >= @from AND created_at < @to",
                new object[]
                {
                    new SqlParameter("@batch", batch),
                    new SqlParameter("@from", SqlDbType.DateTime2) { Value = from },
                    new SqlParameter("@to", SqlDbType.DateTime2) { Value = to },
                }, ct);
            deleted += n;
            if (n < batch) return deleted;
        }
    }

    private async Task ReconcileDayAsync(DateOnly day, CancellationToken ct)
    {
        var from = day.ToDateTime(TimeOnly.MinValue);
        await db.Database.ExecuteSqlRawAsync(ReconcileSql, new object[]
        {
            new SqlParameter("@from", SqlDbType.DateTime2) { Value = from },
            new SqlParameter("@to", SqlDbType.DateTime2) { Value = from.AddDays(1) },
            new SqlParameter("@day", SqlDbType.Date) { Value = from },
        }, ct);
    }
}
