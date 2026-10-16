namespace AiGateway.Core.Provisioning;

/// <summary>
/// Key provider terikat ke host tujuannya. Mengganti base URL ke host lain sambil mempertahankan key yang tersimpan
/// akan mengirim key itu ke host baru (mis. host milik penyerang), jadi perubahan tujuan wajib disertai key baru.
/// </summary>
public static class CredentialTarget
{
    public const string RequiredCode = "credential_required_on_base_url_change";

    public const string RequiredMessage =
        "Mengganti host base URL mengharuskan API key baru pada permintaan yang sama; key lama tidak dikirim ke host lain.";

    /// <summary>
    /// true bila <paramref name="newUrl"/> menuju skema/host/port yang berbeda dari <paramref name="oldUrl"/> (huruf besar/kecil
    /// tidak dibedakan). Perubahan path saja (mis. /v1 menjadi /v2) tetap ke host yang sama sehingga aman. URL lama yang
    /// tidak terbaca dianggap berubah.
    /// </summary>
    public static bool ChangesTarget(string? oldUrl, string newUrl)
    {
        if (!Uri.TryCreate(oldUrl, UriKind.Absolute, out var before) || !Uri.TryCreate(newUrl, UriKind.Absolute, out var after)) return true;
        return !string.Equals(before.GetLeftPart(UriPartial.Authority), after.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }
}
