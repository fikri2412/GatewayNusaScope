using System.Net;
using System.Net.Sockets;
using AiGateway.Core.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Security;

/// <summary>
/// Anti-SSRF untuk seluruh trafik keluar gateway (proxy, discover, sinkronisasi katalog).
/// Dua lapis: validasi statis saat URI masuk dari admin (https, tanpa user/query/fragment, host literal privat
/// ditolak), lalu validasi ulang di <see cref="OutboundSecurity.CreateHandler"/> tepat sebelum socket dibuka —
/// host di-resolve di situ dan hanya alamat yang lolos yang disambung, jadi DNS rebinding dan redirect tidak menolong.
/// </summary>
public sealed class OutboundSecurityPolicy
{
    // Rentang special-purpose IANA yang ditolak (default konservatif). Operator bisa membukanya satu per satu
    // lewat Security:AllowedPrivateNetworks.
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),       // unspecified / "jaringan ini"
        IPNetwork.Parse("10.0.0.0/8"),      // privat
        IPNetwork.Parse("100.64.0.0/10"),   // CGNAT
        IPNetwork.Parse("127.0.0.0/8"),     // loopback
        IPNetwork.Parse("169.254.0.0/16"),  // link-local
        IPNetwork.Parse("172.16.0.0/12"),   // privat
        IPNetwork.Parse("192.0.0.0/24"),    // penugasan protokol IETF
        IPNetwork.Parse("192.0.2.0/24"),    // dokumentasi
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
        IPNetwork.Parse("2001:db8::/32"),   // dokumentasi
        IPNetwork.Parse("fc00::/7"),        // unique local
        IPNetwork.Parse("fec0::/10"),       // site-local (usang)
        IPNetwork.Parse("fe80::/10"),       // link-local
        IPNetwork.Parse("ff00::/8"),        // multicast
    ];

    private readonly IPNetwork[] _allowedNetworks;
    private readonly HashSet<string> _allowedHosts;

    public OutboundSecurityPolicy(OutboundSecurityOptions options)
    {
        var errors = ValidateOptions(options);
        if (errors.Count > 0)
            throw new InvalidOperationException("Security:AllowedPrivateNetworks tidak valid: " + string.Join(" ", errors));

        var networks = new List<IPNetwork>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in options.AllowedPrivateNetworks ?? [])
        {
            var entry = raw.Trim();
            if (entry.Contains('/')) networks.Add(IPNetwork.Parse(entry));
            else if (IPAddress.TryParse(entry, out var ip))
                networks.Add(new IPNetwork(ip, ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
            else hosts.Add(NormalizeHost(entry));
        }
        _allowedNetworks = [.. networks];
        _allowedHosts = hosts;
    }

    /// <summary>Entri yang tidak bisa dibaca membuat startup gagal; juga dipakai validasi options.</summary>
    public static IReadOnlyList<string> ValidateOptions(OutboundSecurityOptions options)
    {
        var errors = new List<string>();
        foreach (var raw in options.AllowedPrivateNetworks ?? [])
        {
            var entry = raw?.Trim() ?? "";
            var valid = entry.Length > 0 && (entry.Contains('/')
                ? IPNetwork.TryParse(entry, out _)
                : IPAddress.TryParse(entry, out _) || (entry.Length <= 253 && Uri.CheckHostName(entry) == UriHostNameType.Dns));
            if (!valid) errors.Add($"'{raw}' bukan CIDR, IP, atau nama host yang valid.");
        }
        return errors;
    }

    public bool IsHostAllowed(string host) => _allowedHosts.Contains(NormalizeHost(host));

    /// <summary>Alamat publik selalu boleh; privat/loopback/link-local/multicast hanya lewat konfigurasi eksplisit.</summary>
    public bool IsAddressAllowed(IPAddress address)
    {
        foreach (var network in _allowedNetworks)
            if (Matches(network, address)) return true;
        return !IsBlocked(address);
    }

    private static bool Matches(IPNetwork network, IPAddress address)
    {
        if (network.BaseAddress.AddressFamily == address.AddressFamily) return network.Contains(address);
        if (!address.IsIPv4MappedToIPv6) return false;
        var v4 = address.MapToIPv4();
        return network.BaseAddress.AddressFamily == v4.AddressFamily && network.Contains(v4);
    }

    private static bool IsBlocked(IPAddress address)
    {
        // ::ffff:10.0.0.1 mewakili 10.0.0.1; dinormalkan dulu supaya daftar rentang IPv4 tetap berlaku.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var network in BlockedNetworks)
            if (network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address)) return true;
        return false;
    }

    /// <summary>Validasi statis di batas kepercayaan: dipakai saat provider/katalog dikonfigurasi (tanpa lookup DNS).</summary>
    public Uri RequireHttpsUri(string url, string code, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > maxLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw GatewayException.BadRequest(code, "URL harus https mutlak tanpa user, query, atau fragment.");
        return RequireHttpsUri(uri, code);
    }

    /// <inheritdoc cref="RequireHttpsUri(string, string, int)"/>
    public Uri RequireHttpsUri(Uri uri, string code)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.Host.Length == 0 || IsBlockedLiteral(uri))
            throw GatewayException.BadRequest(code, "URL harus https mutlak tanpa user, query, atau fragment, dan host bukan alamat privat.");
        return uri;
    }

    private bool IsBlockedLiteral(Uri uri)
    {
        if (uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)) return false;
        var address = ParseLiteral(uri.DnsSafeHost);
        return address is null || !IsAddressAllowed(address);
    }

    /// <summary>
    /// Resolve host lalu sambung ke alamat pertama yang lolos. Dipanggil handler tepat sebelum socket dibuka,
    /// jadi alamat yang divalidasi adalah alamat yang benar-benar dihubungi.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        IPAddress[] resolved;
        var literal = ParseLiteral(endpoint.Host);
        if (literal is not null) resolved = [literal];
        else
        {
            try { resolved = await Dns.GetHostAddressesAsync(endpoint.Host, ct); }
            catch (SocketException ex) { throw new HttpRequestException($"Host '{endpoint.Host}' tidak bisa di-resolve.", ex); }
        }

        // Host yang diizinkan eksplisit boleh resolve ke alamat apa pun; selain itu alamat dicek ulang di sini.
        var targets = IsHostAllowed(endpoint.Host) ? resolved : resolved.Where(IsAddressAllowed).ToArray();
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

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();
}

public static class OutboundSecurity
{
    /// <summary>Daftarkan options + kebijakan koneksi keluar. Dipanggil sekali dari komposisi akar.</summary>
    public static IServiceCollection AddOutboundSecurity(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<OutboundSecurityOptions>().Bind(config.GetSection(OutboundSecurityOptions.Section))
            .Validate(o =>
            {
                var errors = OutboundSecurityPolicy.ValidateOptions(o);
                return errors.Count == 0;
            })
            .ValidateOnStart();
        services.TryAddSingleton(sp => new OutboundSecurityPolicy(sp.GetRequiredService<IOptions<OutboundSecurityOptions>>().Value));
        return services;
    }

    /// <summary>
    /// Handler upstream: tanpa redirect, tanpa proxy lingkungan, dan <c>ConnectCallback</c> yang me-resolve +
    /// memvalidasi alamat tepat sebelum koneksi. TLS tetap diurus handler terhadap hostname asli permintaan.
    /// Pakai sebagai primary handler klien keluar, mis. <c>ConfigurePrimaryHttpMessageHandler(OutboundSecurity.CreateHandler)</c>.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(IServiceProvider services)
    {
        var policy = services.GetRequiredService<OutboundSecurityPolicy>();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, ct) => policy.ConnectAsync(context.DnsEndPoint, ct),
        };
    }
}
