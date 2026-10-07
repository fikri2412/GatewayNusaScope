using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace AiGateway.Core.Security;

/// <summary>
/// Enkripsi key provider milik tenant. Purpose menyertakan tenant id, jadi ciphertext yang
/// disalin ke tenant lain tidak bisa didekripsi.
/// </summary>
public sealed class ProviderKeyProtector(IDataProtectionProvider provider)
{
    private IDataProtector For(long tenantId) =>
        provider.CreateProtector("AiGateway.ProviderKey.v1", tenantId.ToString(CultureInfo.InvariantCulture));

    public string Protect(long tenantId, string apiKey) => For(tenantId).Protect(apiKey);

    public string Unprotect(long tenantId, string cipher) => For(tenantId).Unprotect(cipher);

    /// <summary>4 karakter terakhir untuk dikenali di UI; key pendek tidak dibocorkan sama sekali.</summary>
    public static string Hint(string apiKey) => apiKey.Length >= 12 ? apiKey[^4..] : "****";
}
