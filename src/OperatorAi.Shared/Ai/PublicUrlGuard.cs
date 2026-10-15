using System.Net;
using System.Net.Sockets;

namespace OperatorAi.Shared.Ai;

/// <summary>
/// Anti-SSRF untuk trafik keluar langsung dari chatbot (fallback pengambilan halaman tanpa kunci). Host di-resolve
/// dan divalidasi di dalam <c>ConnectCallback</c> tepat sebelum socket dibuka, jadi DNS rebinding, redirect,
/// dan proxy lingkungan tidak menolong; hanya alamat publik yang disambung. Daftar rentang special-purpose IANA
/// mengikuti kebijakan keluar gateway (<c>AiGateway.Core.Security.OutboundSecurityPolicy</c>) tanpa allowlist:
/// jalur fallback chatbot selalu publik-saja.
/// </summary>
public static class PublicUrlGuard
{
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),       // unspecified / "jaringan ini"
        IPNetwork.Parse("10.0.0.0/8"),      // privat
        IPNetwork.Parse("100.64.0.0/10"),   // CGNAT
        IPNetwork.Parse("127.0.0.0/8"),     // loopback
        IPNetwork.Parse("169.254.0.0/16"),  // link-local (metadata cloud)
        IPNetwork.Parse("172.16.0.0/12"),   // privat
        IPNetwork.Parse("192.0.0.0/24"),    // penugasan protokol IETF
        IPNetwork.Parse("192.0.2.0/24"),    // dokumentasi
        IPNetwork.Parse("192.88.99.0/24"),  // 6to4 relay anycast (usang)
        IPNetwork.Parse("192.168.0.0/16"),  // privat
        IPNetwork.Parse("198.18.0.0/15"),   // benchmarking
        IPNetwork.Parse("198.51.100.0/24"), // dokumentasi
        IPNetwork.Parse("203.0.113.0/24"),  // dokumentasi
        IPNetwork.Parse("224.0.0.0/4"),     // multicast
        IPNetwork.Parse("240.0.0.0/4"),     // reserved, termasuk 255.255.255.255
        IPNetwork.Parse("::/96"),           // unspecified, ::1, dan IPv4-compatible ::a.b.c.d
        IPNetwork.Parse("64:ff9b::/96"),    // NAT64 well-known
        IPNetwork.Parse("64:ff9b:1::/48"),  // NAT64 local-use
        IPNetwork.Parse("100::/64"),        // discard-only
        IPNetwork.Parse("2001::/23"),       // penugasan protokol IETF, termasuk Teredo 2001::/32
        IPNetwork.Parse("2001:db8::/32"),   // dokumentasi
        IPNetwork.Parse("2002::/16"),       // 6to4 (menyematkan alamat IPv4)
        IPNetwork.Parse("fc00::/7"),        // unique local
        IPNetwork.Parse("fec0::/10"),       // site-local (usang)
        IPNetwork.Parse("fe80::/10"),       // link-local
        IPNetwork.Parse("ff00::/8"),        // multicast
    ];

    /// <summary>Handler keluar: tanpa redirect, tanpa proxy lingkungan, batas koneksi, dan validasi alamat saat connect.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = static (context, ct) => ConnectAsync(context.DnsEndPoint, ct),
    };

    /// <summary>Alamat publik IANA selalu boleh; privat/loopback/link-local/dokumentasi/multicast tidak.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        // ::ffff:10.0.0.1 mewakili 10.0.0.1; dinormalkan dulu supaya daftar rentang IPv4 tetap berlaku.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var network in BlockedNetworks)
            if (network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address)) return false;
        return true;
    }

    /// <summary>
    /// Resolve host lalu sambung ke alamat pertama yang lolos; alamat yang divalidasi adalah alamat yang benar-benar
    /// dihubungi, jadi tidak ada jendela antara pemeriksaan dan koneksi.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        IPAddress[] resolved;
        var literal = ParseLiteral(endpoint.Host);
        if (literal is not null) resolved = [literal];
        else
        {
            try { resolved = await Dns.GetHostAddressesAsync(endpoint.Host, ct); }
            catch (SocketException ex) { throw new HttpRequestException($"Host '{endpoint.Host}' tidak bisa di-resolve.", ex); }
        }

        var targets = resolved.Where(IsPublicAddress).ToArray();
        if (targets.Length == 0)
            throw new HttpRequestException($"Koneksi ke '{endpoint.Host}' ditolak: alamat privat/loopback/link-local tidak diizinkan.");

        SocketException? lastError = null;
        foreach (var address in targets)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) { socket.Dispose(); lastError = ex; }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException($"Koneksi ke '{endpoint.Host}' gagal.", lastError);
    }

    private static IPAddress? ParseLiteral(string host) => IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? ip : null;
}
