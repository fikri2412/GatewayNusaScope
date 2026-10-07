using System.Net;
using System.Text.Json;

namespace AiGateway.Core.Security;

public static class IpAllowList
{
    /// <summary>
    /// <paramref name="allowedIpsJson"/> berisi array string (IP atau CIDR). Kosong/NULL = semua diizinkan.
    /// JSON rusak atau IP klien tak diketahui = ditolak (fail closed).
    /// </summary>
    public static bool IsAllowed(string? allowedIpsJson, IPAddress? clientIp)
    {
        if (string.IsNullOrWhiteSpace(allowedIpsJson)) return true;

        string[] entries;
        try { entries = JsonSerializer.Deserialize<string[]>(allowedIpsJson) ?? []; }
        catch (JsonException) { return false; }
        if (entries.Length == 0) return true;
        if (clientIp is null) return false;

        if (clientIp.IsIPv4MappedToIPv6) clientIp = clientIp.MapToIPv4();
        foreach (var entry in entries)
        {
            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out var net) && ContainsNetwork(net, clientIp)) return true;
            }
            else if (IPAddress.TryParse(entry, out var ip) && Normalize(ip).Equals(clientIp))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>::ffff:1.2.3.4 dan 1.2.3.4 adalah alamat yang sama; <see cref="IPAddress.Equals"/> butuh keluarga yang sama.</summary>
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Rentang IPv4-mapped (::ffff:0:0/96 ke atas) dicocokkan sebagai rentang IPv4-nya agar klien IPv4 ikut cocok.</summary>
    private static bool ContainsNetwork(IPNetwork network, IPAddress client)
    {
        if (!network.BaseAddress.IsIPv4MappedToIPv6) return network.Contains(client);
        // Di bawah /96 cakupannya bukan lagi alamat IPv4; dibiarkan apa adanya supaya tidak melebar jadi semua IPv4.
        if (network.PrefixLength < 96) return network.Contains(client);
        return new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96).Contains(client);
    }
}
