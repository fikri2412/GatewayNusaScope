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
                if (IPNetwork.TryParse(entry, out var net) && net.Contains(clientIp)) return true;
            }
            else if (IPAddress.TryParse(entry, out var ip) && ip.Equals(clientIp))
            {
                return true;
            }
        }
        return false;
    }
}
