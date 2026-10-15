using System.ComponentModel.DataAnnotations;

namespace AiGateway.Core.WebSearch;

/// <summary>Opsi Web Search Hub (bagian "WebSearch" dari konfigurasi).</summary>
public sealed class WebSearchOptions
{
    public const string Section = "WebSearch";
    public const int DefaultProviderTimeoutSeconds = 15;
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Fraksi anggaran yang boleh terpakai sebelum penyedia dikeluarkan dari pemilihan.</summary>
    [Range(0.1, 1.0)] public decimal BudgetThreshold { get; set; } = 0.95m;

    /// <summary>Jumlah kegagalan server beruntun sebelum sirkuit terbuka.</summary>
    [Range(1, 100)] public int CircuitFailureThreshold { get; set; } = 3;

    /// <summary>Cooldown sirkuit seragam untuk kegagalan server beruntun (flat, tidak berlipat).</summary>
    [Range(1, 3600)] public int CircuitCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Cooldown dasar saat penyedia rate limited tanpa Retry-After; berlipat dua tiap 429 beruntun
    /// memakai ConsecutiveFailures, maksimum 32x. Retry-After eksplisit selalu menang.
    /// </summary>
    [Range(1, 3600)] public int RetryAfterFallbackSeconds { get; set; } = 60;

    /// <summary>Masa sewa satu percobaan half-open; setelah itu request lain boleh mencoba lagi.</summary>
    [Range(1, 3600)] public int ProbeLeaseSeconds { get; set; } = 30;

    /// <summary>Jeda pemeriksaan ulang kuota untuk kebijakan reset manual (tanpa tanggal reset otomatis).</summary>
    [Range(1, 720)] public int QuotaRecheckHours { get; set; } = 6;

    /// <summary>Batas waktu total per panggilan penyedia (detik); dipakai saat registrasi HttpClient di DI.</summary>
    [Range(1, 600)] public int ProviderTimeoutSeconds { get; set; } = DefaultProviderTimeoutSeconds;

    /// <summary>Batas byte body respons penyedia; respons berlebih ditolak, tidak diparse sebagian.</summary>
    [Range(1024, 268_435_456)] public int ProviderMaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    /// <summary>Batas permintaan search/extract per menit per tenant pada data plane.</summary>
    [Range(1, 100_000)] public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Slug tenant eksplisit, wajib diisi bila Providers tidak kosong.</summary>
    public string? SeedTenantSlug { get; set; }

    /// <summary>Registry penyedia yang di-seed bila tabel konfigurasi masih kosong.</summary>
    public SearchProviderSpec[] Providers { get; set; } = [];
}
