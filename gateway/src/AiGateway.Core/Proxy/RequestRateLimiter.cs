namespace AiGateway.Core.Proxy;

/// <summary>
/// Pembatas request per menit di memori (sliding window counter: jendela sebelumnya dibobotkan sisa waktunya).
/// ponytail: satu lock global dan state per proses. Cukup untuk satu instance; untuk beberapa instance
/// pindahkan hitungan ke penyimpanan bersama (mis. tabel/Redis) dengan antarmuka yang sama.
/// </summary>
public sealed class RequestRateLimiter(TimeProvider clock)
{
    private const int WindowSeconds = 60;
    private const int SweepEvery = 1024;

    private sealed class Window
    {
        public long Index;
        public int Current;
        public int Previous;
    }

    private readonly Dictionary<(string Scope, long Id), Window> _windows = [];
    private readonly object _gate = new();
    private int _calls;

    /// <summary>
    /// Periksa semua batas sekaligus; hitungan hanya naik bila semuanya lolos (permintaan yang ditolak
    /// satu scope tidak menghabiskan jatah scope lain). Bila ditolak, <paramref name="retryAfter"/> = sisa jendela.
    /// </summary>
    public bool TryAcquire(IReadOnlyList<(string Scope, long Id, int Limit)> checks, out TimeSpan retryAfter)
    {
        var now = clock.GetUtcNow();
        var seconds = now.ToUnixTimeMilliseconds() / 1000.0;
        var index = (long)(seconds / WindowSeconds);
        var elapsed = (seconds - index * WindowSeconds) / WindowSeconds; // 0..1 dalam jendela berjalan
        retryAfter = TimeSpan.Zero;

        lock (_gate)
        {
            if (++_calls % SweepEvery == 0)
                foreach (var stale in _windows.Where(w => w.Value.Index < index - 1).Select(w => w.Key).ToList())
                    _windows.Remove(stale);

            var touched = new List<Window>(checks.Count);
            foreach (var (scope, id, limit) in checks)
            {
                var w = Advance(scope, id, index);
                touched.Add(w);
                if (w.Previous * (1 - elapsed) + w.Current + 1 > limit)
                {
                    retryAfter = TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((1 - elapsed) * WindowSeconds)));
                    return false;
                }
            }
            foreach (var w in touched) w.Current++;
            return true;
        }
    }

    private Window Advance(string scope, long id, long index)
    {
        if (!_windows.TryGetValue((scope, id), out var w))
            _windows[(scope, id)] = w = new Window { Index = index };
        if (w.Index == index) return w;
        w.Previous = w.Index == index - 1 ? w.Current : 0;
        w.Current = 0;
        w.Index = index;
        return w;
    }
}
