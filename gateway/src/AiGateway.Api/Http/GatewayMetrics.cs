using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiGateway.Core.Maintenance;
using Microsoft.Extensions.Options;

namespace AiGateway.Api.Http;

/// <summary>
/// GB-05: exporter metrik opsional, <b>nonaktif secara default</b>. Bila <see cref="Enabled"/>, <see cref="BearerToken"/> wajib
/// (secret: user-secrets atau variabel lingkungan) dan endpoint hanya menjawab permintaan ber-bearer yang cocok.
/// </summary>
public sealed class MetricsOptions
{
    public const string Section = "Metrics";
    public bool Enabled { get; set; }
    /// <summary>Path scrape; tidak boleh berada di bawah <c>/v1</c>, <c>/admin</c>, <c>/client</c>, atau <c>/platform</c>.</summary>
    [Required] public string Path { get; set; } = "/metrics";
    /// <summary>Token bearer untuk scraper (minimal 24 karakter bila <see cref="Enabled"/>). Jangan taruh di file yang di-commit.</summary>
    public string? BearerToken { get; set; }

    public const int MinTokenLength = 24;

    /// <summary>Kesalahan konfigurasi (kosong bila valid); entri aktif tanpa token ditolak saat start.</summary>
    public string? Validate()
    {
        if (!Enabled) return null;
        if (BearerToken is not { Length: >= MinTokenLength })
            return $"Metrics:BearerToken wajib diisi minimal {MinTokenLength} karakter bila Metrics:Enabled=true (endpoint metrik tidak pernah terbuka tanpa autentikasi).";
        if (!Path.StartsWith('/') || Path.Length < 2 || Path.Contains("//", StringComparison.Ordinal))
            return "Metrics:Path harus diawali '/' dan bukan root.";
        foreach (var reserved in new[] { "/v1", "/admin", "/client", "/platform", "/healthz" })
            if (Path.Equals(reserved, StringComparison.OrdinalIgnoreCase) || Path.StartsWith(reserved + "/", StringComparison.OrdinalIgnoreCase))
                return $"Metrics:Path tidak boleh berada di bawah {reserved}.";
        return null;
    }
}

/// <summary>
/// Pengumpul metrik di memori dari <c>System.Diagnostics.Metrics</c> tanpa dependency baru, dengan keluaran teks Prometheus.
/// Kardinalitas dibatasi oleh konstruksi: hanya meter dan label yang di-allowlist (<c>area</c>, <c>status</c>, <c>operation</c>,
/// <c>provider</c>) yang diteruskan — label lain (tenant, key, model, path, dst.) dibuang walau suatu hari ditambahkan ke instrumen —
/// panjang nilai label dibatasi, dan jumlah seri per instrumen dibatasi (kelebihan dihitung di <c>gateway_metrics_dropped_total</c>).
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    public static readonly IReadOnlySet<string> AllowedMeters = new HashSet<string> { "AiGateway.Http", "AiGateway.WebSearch" };
    public static readonly IReadOnlySet<string> AllowedLabels = new HashSet<string> { "area", "status", "operation", "provider" };
    public const int MaxSeriesPerMetric = 200;
    public const int MaxLabelValueLength = 40;

    private static readonly double[] Buckets = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    private sealed class Series
    {
        public readonly object Lock = new();
        public long Count;
        public double Sum;
        public readonly long[] BucketCounts = new long[Buckets.Length];
    }

    private sealed record Meta(string Metric, bool Histogram);

    private readonly MeterListener listener = new();
    private readonly ConcurrentDictionary<string, Meta> metas = new();
    private readonly ConcurrentDictionary<(string Metric, string Labels), Series> series = new();
    private readonly ConcurrentDictionary<string, int> seriesPerMetric = new();
    private readonly object createLock = new();
    private long dropped;

    public MetricsCollector()
    {
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (!AllowedMeters.Contains(instrument.Meter.Name)) return;
            if (instrument is Counter<long> or Histogram<double>) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var meta = metas.GetOrAdd(instrument.Name, _ => new Meta(MetricName(instrument), instrument is Histogram<double>));
        var labels = Labels(tags);
        var key = (meta.Metric, labels);
        if (!series.TryGetValue(key, out var cell))
        {
            // Seri baru jarang dibuat; kunci kecil menjaga hitungan per metrik tepat walau dua thread membuat seri yang sama.
            lock (createLock)
            {
                if (!series.TryGetValue(key, out cell))
                {
                    var count = seriesPerMetric.GetValueOrDefault(meta.Metric);
                    if (count >= MaxSeriesPerMetric)
                    {
                        Interlocked.Increment(ref dropped);
                        return;
                    }
                    cell = new Series();
                    series[key] = cell;
                    seriesPerMetric[meta.Metric] = count + 1;
                }
            }
        }
        lock (cell.Lock)
        {
            cell.Count++;
            cell.Sum += value;
            if (meta.Histogram)
                for (var i = 0; i < Buckets.Length; i++)
                    if (value <= Buckets[i]) { cell.BucketCounts[i]++; break; } // kumulatif dihitung saat render
        }
    }

    /// <summary><c>gateway.http.duration</c> (unit ms) → <c>gateway_http_duration_milliseconds</c>; counter → <c>…_total</c>.</summary>
    private static string MetricName(Instrument instrument)
    {
        var name = instrument.Name.Replace('.', '_').Replace('-', '_');
        if (instrument is Histogram<double>) return name + (instrument.Unit == "ms" ? "_milliseconds" : "");
        return name.EndsWith("_total", StringComparison.Ordinal) ? name : name + "_total";
    }

    private static string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var pairs = new List<(string Key, string Value)>();
        foreach (var tag in tags)
        {
            if (!AllowedLabels.Contains(tag.Key)) continue;
            var value = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? "";
            pairs.Add((tag.Key, value.Length <= MaxLabelValueLength ? value : value[..MaxLabelValueLength]));
        }
        pairs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return string.Join(',', pairs.Select(p => $"{p.Key}=\"{Escape(p.Value)}\""));
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Join(string labels, string extra) => labels.Length == 0 ? $"{{{extra}}}" : $"{{{labels},{extra}}}";

    private static string Wrap(string labels) => labels.Length == 0 ? "" : $"{{{labels}}}";

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Teks Prometheus (versi 0.0.4); <paramref name="heartbeat"/> menambah gauge worker maintenance bila diberikan.</summary>
    public string Render(MaintenanceHeartbeat? heartbeat = null, TimeProvider? clock = null)
    {
        var sb = new StringBuilder();
        foreach (var group in series.GroupBy(kv => kv.Key.Metric).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var histogram = metas.Values.Any(m => m.Metric == group.Key && m.Histogram);
            sb.Append("# TYPE ").Append(group.Key).Append(histogram ? " histogram\n" : " counter\n");
            foreach (var (key, cell) in group.OrderBy(kv => kv.Key.Labels, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)))
            {
                lock (cell.Lock)
                {
                    if (!histogram)
                    {
                        sb.Append(group.Key).Append(Wrap(key.Labels)).Append(' ').Append(Number(cell.Sum)).Append('\n');
                        continue;
                    }
                    long cumulative = 0;
                    for (var i = 0; i < Buckets.Length; i++)
                    {
                        cumulative += cell.BucketCounts[i];
                        sb.Append(group.Key).Append("_bucket").Append(Join(key.Labels, $"le=\"{Number(Buckets[i])}\"")).Append(' ')
                            .Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    }
                    sb.Append(group.Key).Append("_bucket").Append(Join(key.Labels, "le=\"+Inf\"")).Append(' ')
                        .Append(cell.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append(group.Key).Append("_sum").Append(Wrap(key.Labels)).Append(' ').Append(Number(cell.Sum)).Append('\n');
                    sb.Append(group.Key).Append("_count").Append(Wrap(key.Labels)).Append(' ')
                        .Append(cell.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        sb.Append("# TYPE gateway_metrics_dropped_total counter\n")
            .Append("gateway_metrics_dropped_total ").Append(Interlocked.Read(ref dropped).ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (heartbeat is not null)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            sb.Append("# TYPE gateway_uptime_seconds gauge\n")
                .Append("gateway_uptime_seconds ").Append(Number(Math.Max(0, (now - heartbeat.StartedAt).TotalSeconds))).Append('\n');
            sb.Append("# TYPE gateway_maintenance_last_success_timestamp_seconds gauge\n");
            foreach (var (job, at) in heartbeat.Snapshot().OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append("gateway_maintenance_last_success_timestamp_seconds{job=\"").Append(Escape(job)).Append("\"} ")
                    .Append(at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    public void Dispose() => listener.Dispose();
}

public static class GatewayMetrics
{
    public static IServiceCollection AddGatewayMetrics(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<MetricsOptions>().Bind(config.GetSection(MetricsOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.Validate() is null, "Konfigurasi Metrics tidak valid. Metrics:Enabled=true memerlukan Metrics:BearerToken (>= 24 karakter) dan Metrics:Path yang aman.")
            .ValidateOnStart();
        // Pengumpul hanya dibuat bila fitur aktif: tanpa konfigurasi, tidak ada listener dan perilaku tidak berubah.
        if (config.GetSection(MetricsOptions.Section).GetValue<bool>(nameof(MetricsOptions.Enabled)))
            services.AddSingleton<MetricsCollector>();
        return services;
    }

    public static IEndpointRouteBuilder MapGatewayMetrics(this IEndpointRouteBuilder app)
    {
        // Keputusan satu sumber: collector hanya terdaftar bila fitur aktif (lihat AddGatewayMetrics).
        if (app.ServiceProvider.GetService<MetricsCollector>() is null) return app;
        var options = app.ServiceProvider.GetRequiredService<IOptions<MetricsOptions>>().Value;
        var path = options.Path;
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.BearerToken!));

        app.MapGet(path, (HttpContext http, MetricsCollector collector, MaintenanceHeartbeat heartbeat, TimeProvider clock) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var authorization = http.Request.Headers.Authorization.ToString();
            const string scheme = "Bearer ";
            var presented = authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? authorization[scheme.Length..].Trim() : "";
            // Hash dulu lalu bandingkan constant-time: panjang token yang dicoba tidak bocor lewat waktu respons.
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), tokenHash))
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            return Results.Text(collector.Render(heartbeat, clock), "text/plain; version=0.0.4; charset=utf-8");
        });
        return app;
    }
}
