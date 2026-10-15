using AiGateway.Core.Domain;

namespace AiGateway.Core.WebSearch;

public interface ISearchProvider
{
    string Type { get; }
    SearchCapabilities Capabilities { get; }

    /// <summary>Base URI bawaan (tanpa slash akhir) saat konfigurasi tenant tidak mengisi BaseUri.</summary>
    string DefaultBaseUri => "";

    /// <summary>
    /// Estimasi biaya pencarian dalam satuan biaya penyedia (lihat dokumentasi biaya masing-masing adapter).
    /// <paramref name="resultCount"/> = jumlah hasil yang benar-benar dikembalikan.
    /// </summary>
    decimal EstimateSearchCost(SearchProviderConfig config, SearchRequest request, int resultCount) => config.CostPerSearch;

    /// <summary>
    /// Estimasi biaya ekstraksi dalam satuan biaya penyedia.
    /// <paramref name="successCount"/> = jumlah halaman yang berhasil (halaman gagal tidak ditagih penyedia).
    /// </summary>
    decimal EstimateExtractCost(SearchProviderConfig config, ExtractRequest request, int successCount) =>
        config.CostPerExtract * Math.Max(0, successCount);

    Task<SearchResponse> SearchAsync(SearchProviderConfig config, string apiKey, SearchRequest request, HttpClient client, CancellationToken ct);
    Task<ExtractResponse> ExtractAsync(SearchProviderConfig config, string apiKey, ExtractRequest request, HttpClient client, CancellationToken ct);
    Task<bool> TestAsync(SearchProviderConfig config, string apiKey, HttpClient client, CancellationToken ct);
}

/// <summary>
/// Jumlah halaman yang sudah berhasil diekstrak sebelum kegagalan, untuk akuntansi biaya parsial
/// saat hub harus failover (penyedia sudah menagih halaman yang berhasil).
/// </summary>
public interface IPartialExtractAccounting
{
    int SuccessfulPageCount { get; }
}

public sealed class SearchProviderException(
    System.Net.HttpStatusCode statusCode,
    string responseBody,
    TimeSpan? retryAfter = null,
    string? message = null,
    int successfulPageCount = 0)
    : HttpRequestException(message ?? $"Search provider error (HTTP {(int)statusCode})", null, statusCode), IPartialExtractAccounting
{
    public System.Net.HttpStatusCode ResponseStatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public int SuccessfulPageCount { get; } = successfulPageCount;
}

/// <summary>Respons HTTP 2xx yang tidak sesuai skema penyedia (bukan JSON valid atau bentuk tak dikenal).</summary>
public sealed class SearchProviderProtocolException(string sanitizedMessage, int successfulPageCount = 0)
    : Exception(sanitizedMessage), IPartialExtractAccounting
{
    public int SuccessfulPageCount { get; } = successfulPageCount;
}

/// <summary>
/// Kegagalan transport (timeout/jaringan) atau pembatalan pemanggil <b>setelah</b> sebagian halaman
/// sudah ditagih penyedia; halaman tersebut tetap terbawa untuk akuntansi biaya parsial.
/// Pembatalan disimpan pada <see cref="Exception.InnerException"/> (OperationCanceledException) agar hub
/// dapat meneruskan pembatalan tanpa failover.
/// </summary>
public sealed class SearchProviderTransportException(int successfulPageCount, Exception innerException)
    : Exception("Koneksi ke penyedia gagal.", innerException), IPartialExtractAccounting
{
    public int SuccessfulPageCount { get; } = successfulPageCount;
}
