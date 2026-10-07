namespace AiGateway.Core.Security;

/// <summary>
/// Kebijakan koneksi keluar (anti-SSRF). Default kosong = hanya alamat publik yang boleh dihubungi.
/// Setiap entri adalah CIDR/IP (mis. <c>10.0.0.0/8</c>) atau nama host (mis. <c>internal.example.com</c>)
/// yang secara eksplisit diizinkan walau resolve ke alamat privat/loopback/link-local.
/// </summary>
public sealed class OutboundSecurityOptions
{
    public const string Section = "Security";
    public string[] AllowedPrivateNetworks { get; set; } = [];
}
