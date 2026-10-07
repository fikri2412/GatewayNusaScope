using System.ComponentModel.DataAnnotations;

namespace AiGateway.Core.Domain;

/// <summary>
/// Isi prompt dan respons satu permintaan. Hanya ditulis bila <see cref="Project.LogContent"/> aktif,
/// dengan <see cref="ExpiresAt"/>; dibersihkan oleh retensi.
/// </summary>
public class RequestBody : ITenantOwned
{
    /// <summary>Id <c>usage_logs</c> (tanpa FK: usage_logs juga tanpa FK dan ditulis per permintaan).</summary>
    public long UsageLogId { get; set; }
    public long TenantId { get; set; }
    public string? RequestJson { get; set; }
    public string? ResponseJson { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Aturan alert batas kuota: satu metrik, satu scope (tenant/project/key), satu persentase ambang.</summary>
public class AlertRule : ITenantOwned
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long TenantId { get; set; }
    public required string Name { get; set; }
    /// <summary><see cref="AlertMetrics"/>: daily_tokens, monthly_tokens, atau monthly_budget.</summary>
    public string Metric { get; set; } = AlertMetrics.DailyTokens;
    /// <summary><see cref="PolicyScopes"/>: scope kebijakan yang kuotanya dipantau.</summary>
    public string Scope { get; set; } = PolicyScopes.Tenant;
    /// <summary>Id tenant (scope tenant), project, atau api key; sama dengan konvensi <c>policies.scope_id</c>.</summary>
    public long ScopeId { get; set; }
    /// <summary>Persentase ambang 1-100 (mis. 80 dan 100 sebagai dua aturan).</summary>
    public int ThresholdPercent { get; set; } = 80;
    /// <summary>Webhook tujuan; NULL = hanya tercatat di alert_events.</summary>
    public long? WebhookId { get; set; }
    public bool Enabled { get; set; } = true;
    [Timestamp] public byte[] RowVersion { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Satu kejadian ambang terlewati. Unik per (aturan, periode, ambang) di database.</summary>
public class AlertEvent : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long RuleId { get; set; }
    public string Metric { get; set; } = AlertMetrics.DailyTokens;
    public string Scope { get; set; } = PolicyScopes.Tenant;
    public long ScopeId { get; set; }
    public int ThresholdPercent { get; set; }
    /// <summary>Awal periode: tanggal (harian) atau tanggal 1 bulan berjalan (bulanan).</summary>
    public DateOnly PeriodStart { get; set; }
    public decimal ObservedValue { get; set; }
    public decimal LimitValue { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Endpoint HTTPS tenant yang menerima payload alert; secret penandatangan disimpan terenkripsi.</summary>
public class Webhook : ITenantOwned
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long TenantId { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string SigningSecretEncrypted { get; set; }
    /// <summary>4 karakter terakhir secret untuk dikenali di UI; secret tidak pernah dikembalikan API.</summary>
    public required string SecretHint { get; set; }
    public bool Enabled { get; set; } = true;
    [Timestamp] public byte[] RowVersion { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Antrean pengiriman webhook yang durable: status, percobaan, backoff, dan error terakhir.</summary>
public class WebhookDelivery : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long WebhookId { get; set; }
    public long? AlertEventId { get; set; }
    /// <summary><see cref="DeliveryStatuses"/>: pending, succeeded, failed.</summary>
    public string Status { get; set; } = DeliveryStatuses.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    /// <summary>Payload alert final (tanpa isi prompt atau secret provider).</summary>
    public string? PayloadJson { get; set; }
    public int? ResponseStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
}

/// <summary>Catatan satu eksekusi job terjadwal (retensi, evaluasi alert, pengiriman webhook).</summary>
public class JobRun
{
    public long Id { get; set; }
    public required string JobName { get; set; }
    /// <summary><see cref="JobRunStatuses"/>: running, succeeded, failed.</summary>
    public string Status { get; set; } = JobRunStatuses.Running;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string? Detail { get; set; }
}

public static class AlertMetrics
{
    public const string DailyTokens = "daily_tokens", MonthlyTokens = "monthly_tokens", MonthlyBudget = "monthly_budget";
    public static readonly string[] All = [DailyTokens, MonthlyTokens, MonthlyBudget];
}

public static class DeliveryStatuses
{
    public const string Pending = "pending", Succeeded = "succeeded", Failed = "failed";
}

public static class JobRunStatuses
{
    public const string Running = "running", Succeeded = "succeeded", Failed = "failed";
}
