using System.Collections.Concurrent;
using System.Globalization;
using AiGateway.Core.Options;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Proxy;

/// <summary>
/// GB-01: cooldown route di memori. Route yang gagal karena provider (timeout, tidak terjangkau, 5xx, 429) berturut-turut
/// dilewati sementara supaya request berikutnya tidak menunggu provider yang sama lagi; sesudah cooldown tepat satu
/// request mencoba lagi (half-open) dan hasilnya menentukan pulih atau cooldown baru.
/// <para>
/// State per proses dan per <c>(tenant, route)</c>: tenant lain dan route lain tidak terpengaruh, dan hilang saat proses
/// restart (batas yang diterima; tidak ada penyimpanan bersama antar-instance, sama seperti rate limit).
/// Hanya kegagalan provider yang dihitung; 4xx karena request klien dan kegagalan konfigurasi lokal tidak pernah memicunya.
/// </para>
/// </summary>
public sealed class RouteCooldownTracker(TimeProvider clock, IOptions<ProxyOptions> options)
{
    private const int PruneAbove = 4096;

    private sealed class State
    {
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset? CooldownUntil;
        /// <summary>Selama non-null dan di masa depan, satu request sedang mencoba route yang cooldown-nya sudah habis.</summary>
        public DateTimeOffset? ProbeUntil;
    }

    private readonly ConcurrentDictionary<(long TenantId, long RouteId), State> states = new();

    private ProxyOptions Options => options.Value;
    private bool Enabled => Options.CooldownSeconds > 0;

    /// <summary>
    /// Keadaan route untuk perencanaan: null = boleh dicoba; non-null = sedang cooldown (atau probe-nya sedang berjalan)
    /// sampai waktu itu. Tidak mengubah state.
    /// </summary>
    public DateTimeOffset? CoolingUntil(long tenantId, long routeId)
    {
        if (!Enabled || !states.TryGetValue((tenantId, routeId), out var state)) return null;
        var now = clock.GetUtcNow();
        lock (state)
        {
            if (state.CooldownUntil is { } until && until > now) return until;
            if (state.ProbeUntil is { } probe && probe > now) return probe;
            return null;
        }
    }

    /// <summary>
    /// Mulai satu percobaan di route yang menurut <see cref="CoolingUntil"/> boleh dicoba. Route sehat: selalu true.
    /// Route yang cooldown-nya sudah habis: hanya satu pemanggil yang mendapat <paramref name="probe"/> = true; yang lain
    /// false dan harus melewati route ini.
    /// </summary>
    public bool TryBegin(long tenantId, long routeId, out bool probe)
    {
        probe = false;
        if (!Enabled || !states.TryGetValue((tenantId, routeId), out var state)) return true;
        var now = clock.GetUtcNow();
        lock (state)
        {
            if (state.CooldownUntil is not { } until) return true; // hanya menghitung kegagalan: masih sehat
            if (until > now) return false;
            if (state.ProbeUntil is { } running && running > now) return false;
            // Sewa probe sedikit lebih panjang dari satu percobaan supaya probe yang hilang tidak mengunci route selamanya.
            state.ProbeUntil = now + TimeSpan.FromSeconds(Options.UpstreamTimeoutSeconds + 5);
            probe = true;
            return true;
        }
    }

    /// <summary>Route menjawab: kegagalan beruntun dan cooldown dihapus.</summary>
    public void RecordSuccess(long tenantId, long routeId)
    {
        if (!Enabled) return;
        states.TryRemove((tenantId, routeId), out _);
    }

    /// <summary>
    /// Kegagalan provider. <paramref name="retryAfter"/> (header <c>Retry-After</c> dari 429) dihormati langsung, dibatasi
    /// <see cref="ProxyOptions.CooldownMaxSeconds"/>. Mengembalikan true bila route masuk (atau tetap di) cooldown.
    /// </summary>
    public bool RecordFailure(long tenantId, long routeId, string? retryAfter = null)
    {
        if (!Enabled) return false;
        var now = clock.GetUtcNow();
        var o = Options;
        var state = states.GetOrAdd((tenantId, routeId), _ => new State());
        bool cooling;
        lock (state)
        {
            if (state.ProbeUntil is not null)
            {
                // Probe half-open gagal: langsung cooldown lagi, tanpa menunggu ambang.
                state.ProbeUntil = null;
                state.Failures = Math.Max(state.Failures, o.CooldownFailureThreshold);
            }
            else
            {
                if (now - state.LastFailure > TimeSpan.FromSeconds(o.CooldownWindowSeconds)) state.Failures = 0;
                state.Failures++;
            }
            state.LastFailure = now;

            if (TryParseRetryAfter(retryAfter, now, out var wait) && wait > TimeSpan.Zero)
            {
                var capped = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, o.CooldownMaxSeconds));
                state.CooldownUntil = now + capped;
            }
            else if (state.Failures >= o.CooldownFailureThreshold)
            {
                state.CooldownUntil = now + TimeSpan.FromSeconds(o.CooldownSeconds);
            }
            cooling = state.CooldownUntil is { } until && until > now;
        }
        if (states.Count > PruneAbove) Prune(now);
        return cooling;
    }

    /// <summary>
    /// Percobaan berakhir tanpa kesimpulan tentang kesehatan provider (mis. 4xx klien, klien memutus koneksi, deadline total):
    /// hanya melepas sewa probe milik pemanggil supaya probe berikutnya bisa jalan. Hitungan kegagalan tidak berubah.
    /// </summary>
    public void ReleaseProbe(long tenantId, long routeId)
    {
        if (!states.TryGetValue((tenantId, routeId), out var state)) return;
        lock (state) state.ProbeUntil = null;
    }

    /// <summary><c>Retry-After</c> berbentuk detik atau tanggal HTTP; nilai tak terbaca atau sudah lewat dianggap tidak ada.</summary>
    internal static bool TryParseRetryAfter(string? value, DateTimeOffset now, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            wait = TimeSpan.FromSeconds(seconds);
            return true;
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            wait = date - now;
            return wait > TimeSpan.Zero;
        }
        return false;
    }

    /// <summary>ponytail: route yang dihapus meninggalkan entri kecil; pangkas saat terlalu banyak (upgrade: bersihkan lewat MaintenanceWorker).</summary>
    private void Prune(DateTimeOffset now)
    {
        var window = TimeSpan.FromSeconds(Options.CooldownWindowSeconds);
        foreach (var (key, state) in states)
        {
            lock (state)
            {
                var idle = (state.CooldownUntil ?? DateTimeOffset.MinValue) < now && state.ProbeUntil is null
                           && now - state.LastFailure > window;
                if (idle) states.TryRemove(key, out _);
            }
        }
    }
}
