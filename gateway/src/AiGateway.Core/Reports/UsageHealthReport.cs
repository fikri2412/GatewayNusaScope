using System.Data;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Reports;

/// <summary>
/// Satu baris kesehatan (per model atau per provider) yang diturunkan dari <c>usage_logs</c>. <see cref="Id"/> adalah GUID
/// publik (null bila baris resource sudah dihapus). Persentil dan rasio null bila datanya tidak ada (bukan 0).
/// </summary>
public sealed record HealthRow(
    Guid? Id, string Label, long Requests, long Succeeded, long Failures, double ErrorRate, double? FallbackRate,
    int? LatencyP50Ms, int? LatencyP95Ms, int? TtfbP50Ms, int? TtfbP95Ms, bool SmallSample);

/// <summary>Kesehatan dalam satu jendela waktu. Daftar kosong berarti tidak ada request yang diteruskan ke provider pada jendela itu.</summary>
public sealed record UsageHealth(
    string Window, DateTime From, DateTime To, int MinSample, IReadOnlyList<HealthRow> Models, IReadOnlyList<HealthRow> Providers);

/// <summary>
/// GB-03: kesehatan model dan provider dari log request, dihitung di SQL (bukan memuat log ke memori). Ini jejak pemakaian
/// nyata, **bukan probe aktif**: provider yang tidak dipakai tidak muncul, dan status tidak berubah sampai ada request.
/// </summary>
/// <remarks>
/// Definisi: hanya request yang benar-benar diteruskan ke provider (<c>attempts &gt; 0</c>) dan bukan pembatalan klien
/// (499). Penolakan kebijakan/otentikasi dan kegagalan sebelum provider tidak masuk statistik upstream. Kegagalan provider =
/// status <c>error</c> dengan HTTP ≥ 500, 429, stream yang putus/timeout/terlalu besar, atau respons terlalu besar; 4xx dari
/// request klien yang diteruskan apa adanya tidak dihitung kegagalan (provider menjawab). Persentil latensi dan TTFB hanya
/// dari request sukses. Untuk provider, baris dihubungkan ke provider yang menghasilkan jawaban akhir (percobaan yang gagal
/// lalu beralih ke route lain tidak tercatat sendiri, lihat catatan <c>ponytail</c> di <see cref="Proxy.ChatProxy"/>).
/// </remarks>
public sealed class UsageHealthReport(GatewayDbContext db, TimeProvider clock)
{
    /// <summary>Jendela yang diterima; maksimum 7 hari.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Windows = new Dictionary<string, TimeSpan>
    {
        ["1h"] = TimeSpan.FromHours(1),
        ["6h"] = TimeSpan.FromHours(6),
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
    };

    public const string DefaultWindow = "24h";

    /// <summary>Di bawah jumlah request ini angka ditandai sampel kecil.</summary>
    public const int MinSample = 30;

    /// <summary>
    /// Baris yang dihitung: diteruskan ke provider, bukan pembatalan klien, dan membawa grup. Dipakai dua kali (agregat dan
    /// persentil) sebagai tabel turunan, bukan CTE: EF membungkus SQL mentah sebagai subquery dan SQL Server menolak
    /// <c>WITH</c> di dalam subquery.
    /// </summary>
    private const string Base = """
        SELECT __COL__ AS gid, status, http_status, denied_reason, fallback_used, latency_ms, ttfb_ms
        FROM usage_logs
        WHERE tenant_id = @tenant AND created_at >= @from AND created_at < @to
          AND attempts > 0 AND http_status <> 499 AND __COL__ IS NOT NULL
        """;

    private const string Sql = """
        SELECT a.gid AS [Id], a.requests AS [Requests], a.succeeded AS [Succeeded], a.failures AS [Failures], a.fallbacks AS [Fallbacks],
               p.lat50 AS [Lat50], p.lat95 AS [Lat95], p.ttfb50 AS [Ttfb50], p.ttfb95 AS [Ttfb95]
        FROM (
            SELECT gid,
                   COUNT_BIG(*) AS requests,
                   CAST(SUM(CASE WHEN status = 'ok' THEN 1 ELSE 0 END) AS bigint) AS succeeded,
                   CAST(SUM(CASE WHEN status = 'error' AND (http_status >= 500 OR http_status = 429
                                 OR denied_reason LIKE 'upstream[_]stream[_]%' OR denied_reason = 'upstream_response_too_large')
                            THEN 1 ELSE 0 END) AS bigint) AS failures,
                   CAST(SUM(CASE WHEN status = 'ok' AND fallback_used = 1 THEN 1 ELSE 0 END) AS bigint) AS fallbacks
            FROM (__BASE__) AS b1
            GROUP BY gid
        ) AS a
        LEFT JOIN (
            SELECT DISTINCT gid,
                   PERCENTILE_CONT(0.5)  WITHIN GROUP (ORDER BY CASE WHEN status = 'ok' THEN latency_ms END) OVER (PARTITION BY gid) AS lat50,
                   PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY CASE WHEN status = 'ok' THEN latency_ms END) OVER (PARTITION BY gid) AS lat95,
                   PERCENTILE_CONT(0.5)  WITHIN GROUP (ORDER BY CASE WHEN status = 'ok' THEN ttfb_ms END) OVER (PARTITION BY gid) AS ttfb50,
                   PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY CASE WHEN status = 'ok' THEN ttfb_ms END) OVER (PARTITION BY gid) AS ttfb95
            FROM (__BASE__) AS b2
        ) AS p ON p.gid = a.gid
        """;

    /// <summary>Hasil mentah satu grup; properti sama dengan nama kolom <see cref="Sql"/>.</summary>
    internal sealed class Raw
    {
        public long Id { get; set; }
        public long Requests { get; set; }
        public long Succeeded { get; set; }
        public long Failures { get; set; }
        public long Fallbacks { get; set; }
        public double? Lat50 { get; set; }
        public double? Lat95 { get; set; }
        public double? Ttfb50 { get; set; }
        public double? Ttfb95 { get; set; }
    }

    public async Task<UsageHealth> RunAsync(string? window, CancellationToken ct)
    {
        var key = string.IsNullOrEmpty(window) ? DefaultWindow : window;
        if (!Windows.TryGetValue(key, out var span))
            throw GatewayException.BadRequest("invalid_window", $"window harus salah satu dari {string.Join(", ", Windows.Keys)}.");
        var to = clock.GetUtcNow().UtcDateTime;
        var from = to - span;
        var tenant = db.CurrentTenantId;

        var models = await QueryAsync("model_id", tenant, from, to, ct);
        var providers = await QueryAsync("provider_id", tenant, from, to, ct);

        var modelIds = models.Select(r => r.Id).ToList();
        var providerIds = providers.Select(r => r.Id).ToList();
        var modelNames = await db.Models.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenant && modelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => (Name: m.Alias, Public: m.PublicId), ct);
        var providerNames = await db.Providers.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenant && providerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => (Name: p.Name, Public: p.PublicId), ct);

        return new UsageHealth(key, from, to, MinSample,
            Map(models, modelNames),
            Map(providers, providerNames));
    }

    private async Task<List<Raw>> QueryAsync(string column, long tenant, DateTime from, DateTime to, CancellationToken ct)
    {
        // `column` hanya konstanta internal di atas (model_id/provider_id), tidak pernah berasal dari input.
        var sql = Sql.Replace("__BASE__", Base).Replace("__COL__", column);
        return await db.Database.SqlQueryRaw<Raw>(sql,
            new SqlParameter("@tenant", tenant),
            new SqlParameter("@from", SqlDbType.DateTime2) { Value = from },
            new SqlParameter("@to", SqlDbType.DateTime2) { Value = to }).ToListAsync(ct);
    }

    private static List<HealthRow> Map(List<Raw> rows, Dictionary<long, (string Name, Guid Public)> names) =>
        rows.Select(r => new HealthRow(
                names.TryGetValue(r.Id, out var n) ? n.Public : null,
                names.TryGetValue(r.Id, out var named) ? named.Name : "(dihapus)",
                r.Requests, r.Succeeded, r.Failures,
                (double)r.Failures / r.Requests,
                r.Succeeded > 0 ? (double)r.Fallbacks / r.Succeeded : null,
                Ms(r.Lat50), Ms(r.Lat95), Ms(r.Ttfb50), Ms(r.Ttfb95),
                r.Requests < MinSample))
            .OrderByDescending(h => h.Requests).ThenBy(h => h.Label, StringComparer.Ordinal).ToList();

    private static int? Ms(double? value) => value is { } v ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : null;
}
