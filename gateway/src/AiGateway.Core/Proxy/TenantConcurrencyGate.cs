namespace AiGateway.Core.Proxy;

/// <summary>
/// Batas permintaan chat yang sedang berjalan bersamaan per tenant (GATEWAY.md bagian 8: "jumlah permintaan
/// bersamaan per tenant"). Satu kunci per tenant; entri dihapus begitu hitungannya nol sehingga kamus tidak tumbuh.
/// ponytail: state per proses dan satu lock global - akurat untuk satu instance gateway; beberapa instance perlu
/// penghitung bersama (mis. tabel/Redis) dengan antarmuka yang sama.
/// </summary>
public sealed class TenantConcurrencyGate
{
    private readonly Dictionary<long, int> _active = [];
    private readonly object _gate = new();

    /// <summary>Ambil satu slot untuk tenant; false (tanpa menunggu) bila <paramref name="limit"/> slot sedang terpakai.</summary>
    public bool TryAcquire(long tenantId, int limit)
    {
        lock (_gate)
        {
            _active.TryGetValue(tenantId, out var current);
            if (current >= limit) return false;
            _active[tenantId] = current + 1;
            return true;
        }
    }

    /// <summary>Kembalikan slot yang sudah diambil <see cref="TryAcquire"/>; aman dipanggil berulang/berlebih.</summary>
    public void Release(long tenantId)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(tenantId, out var current) || current <= 1) _active.Remove(tenantId);
            else _active[tenantId] = current - 1;
        }
    }
}
