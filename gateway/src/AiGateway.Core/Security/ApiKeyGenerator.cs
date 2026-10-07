using System.Security.Cryptography;
using System.Text;

namespace AiGateway.Core.Security;

/// <summary>Format key: <c>gw_&lt;10 hex&gt;_&lt;secret base64url 43&gt;</c>. Hanya hash SHA-256 yang disimpan.</summary>
public static class ApiKeyGenerator
{
    private const int PrefixLength = 13; // "gw_" + 10 hex
    private const int MaxKeyLength = 200;

    public static (string Key, string KeyPrefix, string Hash) Generate()
    {
        var keyPrefix = "gw_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = $"{keyPrefix}_{secret}";
        return (key, keyPrefix, Hash(key));
    }

    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Bandingkan hash key yang diberikan dengan hash tersimpan dalam waktu konstan.</summary>
    public static bool Matches(string key, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(storedHash));

    /// <summary>Ambil prefix pencarian dari key; false bila bentuknya tidak mungkin key valid.</summary>
    public static bool TryGetPrefix(string key, out string keyPrefix)
    {
        keyPrefix = "";
        if (key.Length <= PrefixLength + 1 || key.Length > MaxKeyLength) return false;
        if (!key.StartsWith("gw_", StringComparison.Ordinal) || key[PrefixLength] != '_') return false;
        foreach (var c in key.AsSpan(3, 10))
            if (!char.IsAsciiHexDigitLower(c)) return false;
        keyPrefix = key[..PrefixLength];
        return true;
    }
}
