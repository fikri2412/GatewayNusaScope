using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace AiGateway.Core.Maintenance;

/// <summary>
/// Enkripsi secret penandatangan webhook milik tenant. Purpose menyertakan tenant id, jadi ciphertext yang
/// disalin ke tenant lain tidak bisa didekripsi.
/// </summary>
public sealed class WebhookSecretProtector(IDataProtectionProvider provider)
{
    private IDataProtector For(long tenantId) =>
        provider.CreateProtector("AiGateway.WebhookSecret.v1", tenantId.ToString(CultureInfo.InvariantCulture));

    public string Protect(long tenantId, string secret) => For(tenantId).Protect(secret);

    public string Unprotect(long tenantId, string cipher) => For(tenantId).Unprotect(cipher);
}
