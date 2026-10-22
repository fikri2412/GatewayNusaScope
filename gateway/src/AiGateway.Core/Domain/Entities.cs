using System.ComponentModel.DataAnnotations;

namespace AiGateway.Core.Domain;

/// <summary>Entity milik satu tenant. TenantId diisi dari konteks, tidak pernah dari klien.</summary>
public interface ITenantOwned
{
    long TenantId { get; set; }
}

public class Plan
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public int? MaxProjects { get; set; }
    public int? MaxApiKeys { get; set; }
    public long? MaxRequestsPerMonth { get; set; }
    public long? MaxTokensPerMonth { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Tenant
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string Status { get; set; } = TenantStatuses.Active;
    public long PlanId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class User
{
    public long Id { get; set; }
    /// <summary>NULL untuk platform admin.</summary>
    public long? TenantId { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string DisplayName { get; set; }
    public required string Role { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RefreshToken
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public long? ReplacedById { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UserToken
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public required string Purpose { get; set; }
    public required string TokenHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Master data (tingkat platform): resep koneksi ke satu penyedia AI.</summary>
public class ProviderTemplate
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string Type { get; set; } = ProviderTypes.OpenAi;
    public required string DefaultBaseUrl { get; set; }
    public string AuthHeader { get; set; } = "Authorization";
    public string AuthPrefix { get; set; } = "Bearer ";
    public string? ModelsPath { get; set; }
    /// <summary>Cara menarik katalog dari penyedia (<see cref="SyncKinds"/>); NULL = tidak ada sinkronisasi.</summary>
    public string? SyncKind { get; set; }
    public string? SyncUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Master data (tingkat platform): model yang dikenal pada satu template.</summary>
public class CatalogModel
{
    public long Id { get; set; }
    public long TemplateId { get; set; }
    public required string UpstreamModel { get; set; }
    public required string DisplayName { get; set; }
    public int? ContextWindow { get; set; }
    public int? MaxOutputTokens { get; set; }
    public decimal? InputPricePer1M { get; set; }
    public decimal? OutputPricePer1M { get; set; }
    public string Currency { get; set; } = "USD";
    public bool SupportsTools { get; set; }
    public bool SupportsReasoning { get; set; }
    public bool SupportsVision { get; set; }
    /// <summary>Keluarga API upstream model ini (<see cref="ApiFamilies"/>).</summary>
    public string ApiFamily { get; set; } = ApiFamilies.OpenAiChat;
    public int? MaxInputTokens { get; set; }
    public decimal? CacheReadPricePer1M { get; set; }
    public decimal? CacheWritePricePer1M { get; set; }
    /// <summary>Tier harga tambahan (array <c>PriceTier</c>) untuk prompt panjang.</summary>
    public string? ExtraTiersJson { get; set; }
    public string Source { get; set; } = CatalogSources.Manual;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Provider : ITenantOwned
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long TenantId { get; set; }
    public required string Name { get; set; }
    /// <summary>Template asal koneksi; NULL untuk provider custom.</summary>
    public long? TemplateId { get; set; }
    public string Type { get; set; } = ProviderTypes.OpenAi;
    public required string BaseUrl { get; set; }
    public string AuthHeader { get; set; } = "Authorization";
    public string AuthPrefix { get; set; } = "Bearer ";
    /// <summary>Path daftar model (mis. "models"); NULL bila provider tidak menyediakannya.</summary>
    public string? ModelsPath { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? DeletedAt { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ProviderCredential : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long ProviderId { get; set; }
    public required string ApiKeyEncrypted { get; set; }
    public required string KeyHint { get; set; }
    public string Status { get; set; } = CredentialStatuses.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DisabledAt { get; set; }
}

public class Model : ITenantOwned
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long TenantId { get; set; }
    public required string Alias { get; set; }
    public string? Description { get; set; }
    public int? MaxOutputTokens { get; set; }
    /// <summary>Konfirmasi admin untuk SELURUH route model ini: NULL = belum dikonfirmasi (baris lama/import yang belum diuji).</summary>
    public bool? SupportsTools { get; set; }
    public bool? SupportsStreaming { get; set; }
    /// <summary>Konfirmasi admin bahwa model menerima gambar (PNG/JPEG inline); NULL = belum dikonfirmasi.</summary>
    public bool? SupportsVision { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? DeletedAt { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ModelRoute : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long ModelId { get; set; }
    public long ProviderId { get; set; }
    public required string UpstreamModel { get; set; }
    public int Priority { get; set; }
    public int Weight { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public class ModelPrice : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long ModelId { get; set; }
    public decimal InputPricePer1M { get; set; }
    public decimal OutputPricePer1M { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal? CacheReadPricePer1M { get; set; }
    public decimal? CacheWritePricePer1M { get; set; }
    /// <summary>Tier berlaku bila token prompt >= nilai ini (0 = tier dasar). Satu <see cref="EffectiveFrom"/> bisa punya beberapa tier.</summary>
    public int MinInputTokens { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
}

public class Project : ITenantOwned
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long TenantId { get; set; }
    public required string Name { get; set; }
    public string Status { get; set; } = ProjectStatuses.Active;
    public bool LogContent { get; set; }
    public int? ContentRetentionDays { get; set; }
    public DateTime? DeletedAt { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ApiKey : ITenantOwned
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long TenantId { get; set; }
    public long ProjectId { get; set; }
    public required string Name { get; set; }
    public required string KeyPrefix { get; set; }
    public required string KeyHash { get; set; }
    public string? AllowedIpsJson { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public long? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Policy : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public required string Scope { get; set; }
    /// <summary>Id tenant (scope tenant), project, atau api key sesuai <see cref="Scope"/>.</summary>
    public long ScopeId { get; set; }
    public int? MaxTokensPerRequest { get; set; }
    public int? RequestsPerMinute { get; set; }
    public long? DailyTokenQuota { get; set; }
    public long? MonthlyTokenQuota { get; set; }
    public decimal? MonthlyBudget { get; set; }
    public string? AllowedModelsJson { get; set; }
    public bool Enabled { get; set; } = true;
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Pengaturan key/value milik tenant untuk aplikasi klien (mis. chatbot), dikelola dari UI dan dibaca klien lewat
/// API key. Nilai disimpan polos: bukan tempat secret. Key tidak peka huruf besar/kecil (collation database).
/// </summary>
public class AppSetting : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public required string Namespace { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public required string ValueType { get; set; }
    public string? Description { get; set; }
    public long? UpdatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Laporan status aplikasi klien terakhir per (tenant, API key, namespace): nilai efektif yang diizinkan, metadata
/// key, status sinkronisasi, dan jumlah queue/job dari sisi aplikasi. Satu baris per key+namespace (upsert); tidak
/// berisi secret, isi prompt, atau konten pekerjaan. Kolom JSON sudah divalidasi gateway sebelum disimpan.
/// </summary>
public class AppStatusReport : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long ProjectId { get; set; }
    public long ApiKeyId { get; set; }
    public required string Namespace { get; set; }
    public string? SourceSettingsVersion { get; set; }
    public required string PolicyHash { get; set; }
    public required string PromptHash { get; set; }
    public required string EffectiveValuesJson { get; set; }
    public required string SupportedKeysJson { get; set; }
    public required string SyncStatus { get; set; }
    public DateTime? LastConfigSuccessAt { get; set; }
    public required string RejectedKeysJson { get; set; }
    public required string IgnoredKeysJson { get; set; }
    public string? Reason { get; set; }
    public long QueuePending { get; set; }
    public long QueueProcessing { get; set; }
    public long QueueDone { get; set; }
    public long QueueFailed { get; set; }
    public long JobsPending { get; set; }
    public long JobsProcessing { get; set; }
    public long JobsDone { get; set; }
    public long JobsFailed { get; set; }
    public required string ActivePoliciesJson { get; set; }
    public int ReportIntervalSeconds { get; set; }
    public DateTime ObservedAt { get; set; }
    /// <summary>Jam server gateway saat laporan diterima; otoritatif untuk kesegaran (bukan jam klien).</summary>
    public DateTime ReceivedAt { get; set; }
}

public class UsageLog : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long ProjectId { get; set; }
    public long ApiKeyId { get; set; }
    public long? ModelId { get; set; }
    public long? ProviderId { get; set; }
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public required string Status { get; set; }
    public int HttpStatus { get; set; }
    public string? DeniedReason { get; set; }
    public string? FinishReason { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int ReasoningTokens { get; set; }
    public decimal? InputPriceUsed { get; set; }
    public decimal? OutputPriceUsed { get; set; }
    public decimal Cost { get; set; }
    public string Currency { get; set; } = "USD";
    public int LatencyMs { get; set; }
    public int? TtfbMs { get; set; }
    public int Attempts { get; set; }
    public bool FallbackUsed { get; set; }
    public int? UpstreamStatus { get; set; }
    /// <summary>
    /// True bila token/biaya baris ini tidak terbukti lengkap: upstream tidak pernah melaporkan usage final, usage
    /// parsial, atau stream terputus sebelum usage lengkap. Token tetap angka yang diketahui (batas bawah);
    /// biaya tercatat bukan tagihan final. Penolakan/tanpa percobaan provider tidak pernah incomplete.
    /// </summary>
    public bool UsageIncomplete { get; set; } = true;
    public string? EndUser { get; set; }
    public string? Tags { get; set; }
    /// <summary>
    /// GB-02: id sesi dari klien (header <c>x-opencode-session</c>, lihat <c>SessionIds</c>); null bila klien tidak
    /// mengirimnya. Pengelompokan laporan, bukan identitas atau autentikasi.
    /// </summary>
    public string? SessionId { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UsageDaily : ITenantOwned
{
    public long TenantId { get; set; }
    public long ProjectId { get; set; }
    public long ApiKeyId { get; set; }
    /// <summary>0 bila model belum terselesaikan (misalnya alias tidak dikenal).</summary>
    public long ModelId { get; set; }
    public DateOnly Day { get; set; }
    public long Requests { get; set; }
    public long Denied { get; set; }
    public long Errors { get; set; }
    /// <summary>Request yang usage-nya tidak terbukti lengkap (batas bawah), ikut direkonsiliasi ulang oleh retensi.</summary>
    public long IncompleteUsageRequests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    /// <summary>NULL untuk aksi tingkat platform.</summary>
    public long? TenantId { get; set; }
    public long? UserId { get; set; }
    public required string Action { get; set; }
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? DetailJson { get; set; }
    public string? Ip { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PlatformSetting
{
    public required string Key { get; set; }
    public required string ValueJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SearchProviderConfig : ITenantOwned
{
    public long Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long TenantId { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public required string BaseUri { get; set; }
    public required string ApiKeyEncrypted { get; set; }
    public required string KeyHint { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Weight { get; set; } = 1;
    public string Capabilities { get; set; } = "search,extract";
    public decimal? MonthlyBudget { get; set; }
    public decimal CostPerSearch { get; set; } = 1m;
    public decimal CostPerExtract { get; set; } = 1m;
    public string ResetPolicy { get; set; } = ResetPolicies.FirstOfMonthUtc;
    public string? ErrorMappingOverride { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}

public class SearchProviderState : ITenantOwned
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long ProviderId { get; set; }
    public string Status { get; set; } = SearchProviderStatuses.Available;
    public DateTime? UnavailableUntil { get; set; }
    public decimal UsedThisPeriod { get; set; }
    public DateTime PeriodStart { get; set; } = DateTime.UtcNow;
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
    public long FailoverCount { get; set; }
    public long Generation { get; set; }
    public long AdminGeneration { get; set; }
    public Guid? ProbeLeaseId { get; set; }
    public DateTime? ProbeLeaseUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
