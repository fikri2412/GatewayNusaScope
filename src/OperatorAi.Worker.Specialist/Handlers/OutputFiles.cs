namespace OperatorAi.Worker.Specialist.Handlers;

internal static class OutputFiles
{
    /// <summary>
    /// Nama lampiran hasil dari judul: karakter kontrol dan karakter terlarang di nama file dibuang, titik/spasi tepi
    /// dipangkas. Hanya nama tampilan — path penyimpanan memakai id job, bukan nama ini.
    /// </summary>
    public static string CleanName(string title, string extension, string fallbackName)
    {
        Span<char> buffer = stackalloc char[title.Length];
        var length = 0;
        foreach (var c in title)
            if (!char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)) buffer[length++] = c;
        ReadOnlySpan<char> name = buffer[..length];
        name = name.Trim(" .");
        return string.Concat(name.IsEmpty ? fallbackName : name, extension);
    }
}
