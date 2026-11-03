namespace AiGateway.Core.Common;

/// <summary>
/// Aturan id sesi klien (header <c>x-opencode-session</c>): satu nilai, 1–200 karakter, hanya huruf/angka ASCII dan
/// <c>-</c> <c>_</c> <c>.</c>. Dipakai data plane (validasi masuk) dan laporan (filter), supaya nilai yang tersimpan dan
/// nilai yang dicari selalu memenuhi aturan yang sama.
/// </summary>
public static class SessionIds
{
    public const int MaxLength = 200;

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
