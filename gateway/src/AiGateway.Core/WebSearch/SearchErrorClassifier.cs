using System.Net;
using System.Text.Json;
using AiGateway.Core.Domain;
using Polly.Timeout;

namespace AiGateway.Core.WebSearch;

public enum SearchErrorType
{
    None = 0,
    Exhausted = 1,
    RateLimited = 2,
    Broken = 3,
    RetryableServer = 4,
    ClientError = 5
}

public sealed record ClassifiedError(
    SearchErrorType ErrorType,
    TimeSpan? RetryAfter,
    string SanitizedMessage);

/// <summary>Aturan override eksplisit per penyedia (JSON dari panel admin); dicek sebelum aturan bawaan.</summary>
public sealed class ErrorMappingRules
{
    public int[]? ExhaustedStatusCodes { get; set; }
    public string[]? ExhaustedBodySubstrings { get; set; }
    public int[]? RateLimitStatusCodes { get; set; }
    public int[]? BrokenStatusCodes { get; set; }
}

/// <summary>
/// Pemetaan kegagalan penyedia ke kelas penanganan hub. Aturan bawaan hanya memakai kode status resmi
/// tiap penyedia; pencocokan substring hanya dari override eksplisit admin. Pesan selalu tetap (tanpa body/URL).
/// </summary>
public static class SearchErrorClassifier
{
    private const int MaxOverrideSubstrings = 20;
    private const int MaxOverrideSubstringLength = 100;
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(60);

    public static ClassifiedError Classify(
        string providerType,
        HttpStatusCode? statusCode,
        string? responseBody,
        TimeSpan? retryAfterHeader,
        Exception? exception,
        string? errorMappingOverrideJson)
    {
        var body = responseBody?.Trim() ?? "";
        var code = statusCode.HasValue ? (int)statusCode.Value : 0;

        // Wrapper transport menyimpan kegagalan asli (timeout/jaringan) agar tetap terklasifikasi.
        if (exception is SearchProviderTransportException transport)
            exception = transport.InnerException;

        var fromOverride = ClassifyOverride(errorMappingOverrideJson, body, code, retryAfterHeader);
        if (fromOverride != null) return fromOverride;

        var fromProvider = providerType.ToLowerInvariant() switch
        {
            SearchProviderTypes.Tavily => code switch
            {
                401 => Broken("Kunci API Tavily tidak valid atau dicabut."),
                // 432 = key/plan limit, 433 = pay-as-you-go limit (docs.tavily.com, endpoint search).
                432 or 433 => Exhausted("Kuota atau kredit Tavily habis."),
                429 => RateLimited(retryAfterHeader, "Rate limit Tavily terlampaui."),
                _ => null
            },
            SearchProviderTypes.Exa => code switch
            {
                401 or 403 => Broken("Kunci API Exa tidak valid atau dicabut."),
                402 => Exhausted("Kredit akun Exa habis."),
                429 => RateLimited(retryAfterHeader, "Rate limit Exa terlampaui."),
                _ => null
            },
            SearchProviderTypes.Firecrawl => code switch
            {
                401 or 403 => Broken("Kunci API Firecrawl tidak valid atau dicabut."),
                402 => Exhausted("Kredit akun Firecrawl habis."),
                429 => RateLimited(retryAfterHeader, "Rate limit Firecrawl terlampaui."),
                _ => null
            },
            _ => null
        };
        if (fromProvider != null) return fromProvider;

        // Fallback umum: kode status tanpa pencocokan body.
        if (code is 401 or 403) return Broken("Autentikasi penyedia gagal.");
        if (code == 402) return Exhausted("Kredit akun penyedia habis.");
        if (code == 429) return RateLimited(retryAfterHeader, "Rate limit penyedia terlampaui.");
        if (code is >= 500 and <= 599) return new ClassifiedError(SearchErrorType.RetryableServer, null, $"Server penyedia bermasalah (HTTP {code}).");
        if (code is >= 400 and <= 499) return new ClassifiedError(SearchErrorType.ClientError, null, $"Permintaan ke penyedia ditolak (HTTP {code}).");

        if (exception is TimeoutRejectedException or TaskCanceledException or TimeoutException or OperationCanceledException)
            return new ClassifiedError(SearchErrorType.RetryableServer, null, "Koneksi ke penyedia melebihi batas waktu.");
        if (exception is SearchProviderProtocolException)
            return new ClassifiedError(SearchErrorType.RetryableServer, null, "Respons penyedia tidak sesuai skema.");
        if (exception is HttpRequestException)
            return new ClassifiedError(SearchErrorType.RetryableServer, null, "Gagal menghubungi penyedia (gangguan jaringan).");

        return new ClassifiedError(SearchErrorType.RetryableServer, null, "Kegagalan komunikasi tidak dikenal dengan penyedia.");
    }

    private static ClassifiedError? ClassifyOverride(string? json, string body, int code, TimeSpan? retryAfter)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        ErrorMappingRules? rules;
        try
        {
            rules = JsonSerializer.Deserialize<ErrorMappingRules>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }

        if (rules == null) return null;
        if (rules.BrokenStatusCodes?.Contains(code) == true)
            return Broken("Autentikasi gagal (kunci salah atau dicabut).");
        if (rules.ExhaustedStatusCodes?.Contains(code) == true || MatchesOverrideSubstring(body, rules.ExhaustedBodySubstrings))
            return Exhausted("Kuota atau kredit penyedia habis.");
        if (rules.RateLimitStatusCodes?.Contains(code) == true)
            return RateLimited(retryAfter, "Terlalu banyak permintaan (rate limited).");
        return null;
    }

    private static bool MatchesOverrideSubstring(string body, string[]? phrases)
    {
        if (body.Length == 0 || phrases == null) return false;
        var checkedCount = 0;
        foreach (var phrase in phrases)
        {
            if (string.IsNullOrWhiteSpace(phrase)) continue;
            var trimmed = phrase.Trim();
            if (trimmed.Length > MaxOverrideSubstringLength) continue;
            if (body.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
            if (++checkedCount >= MaxOverrideSubstrings) break;
        }
        return false;
    }

    private static ClassifiedError Broken(string message) =>
        new(SearchErrorType.Broken, null, message);

    private static ClassifiedError Exhausted(string message) =>
        new(SearchErrorType.Exhausted, null, message);

    private static ClassifiedError RateLimited(TimeSpan? retryAfter, string message) =>
        new(SearchErrorType.RateLimited, retryAfter ?? DefaultRateLimitDelay, message);
}
