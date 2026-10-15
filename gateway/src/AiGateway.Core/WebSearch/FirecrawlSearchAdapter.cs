using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.WebSearch;

/// <summary>
/// Firecrawl v2 Search/Scrape. Dokumen resmi: https://docs.firecrawl.dev/api-reference/endpoint/search,
/// https://docs.firecrawl.dev/api-reference/endpoint/scrape, https://docs.firecrawl.dev/features/search#cost-implications
/// (search 2 kredit/10 hasil; scrape 1 kredit/halaman).
/// </summary>
public sealed class FirecrawlSearchAdapter(IOptions<WebSearchOptions>? options = null) : ISearchProvider
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(1,
        options?.Value.ProviderTimeoutSeconds ?? WebSearchOptions.DefaultProviderTimeoutSeconds));
    private readonly int _maxResponseBytes = Math.Max(1024,
        options?.Value.ProviderMaxResponseBytes ?? WebSearchOptions.DefaultMaxResponseBytes);

    public string Type => SearchProviderTypes.Firecrawl;
    public SearchCapabilities Capabilities => SearchCapabilities.All;
    public string DefaultBaseUri => "https://api.firecrawl.dev";

    /// <summary>2 kredit per 10 hasil (dibulatkan ke atas) + 1 kredit per halaman yang di-scrape saat IncludeContent.</summary>
    public decimal EstimateSearchCost(SearchProviderConfig config, SearchRequest request, int resultCount)
    {
        var results = Math.Max(1, resultCount);
        var units = 2m * Math.Ceiling(results / 10m);
        if (request.IncludeContent) units += results;
        return config.CostPerSearch * units;
    }

    /// <summary>1 kredit per halaman berhasil.</summary>
    public decimal EstimateExtractCost(SearchProviderConfig config, ExtractRequest request, int successCount) =>
        config.CostPerExtract * Math.Max(0, successCount);

    public async Task<SearchResponse> SearchAsync(SearchProviderConfig config, string apiKey, SearchRequest request, HttpClient client, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["query"] = request.Query,
            ["limit"] = Math.Clamp(request.MaxResults, 1, 100),
        };
        if (request.IncludeContent)
            payload["scrapeOptions"] = new JsonObject { ["formats"] = MarkdownFormats() };

        using var message = new HttpRequestMessage(HttpMethod.Post, SearchProviderHttp.BuildUri(config.BaseUri, "v2/search"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var doc = await SearchProviderHttp.SendJsonAsync(client, message, _timeout, _maxResponseBytes, ct);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True)
        {
            throw new SearchProviderProtocolException("Respons pencarian Firecrawl menandakan kegagalan.");
        }
        if (!root.TryGetProperty("data", out var data)
            || !SearchProviderHttp.TryGetArray(data, "web", out var items))
        {
            throw new SearchProviderProtocolException("Respons pencarian Firecrawl tidak berisi daftar hasil.");
        }

        var results = new List<SearchResultItem>();
        foreach (var item in items.EnumerateArray())
        {
            var snippet = SearchProviderHttp.Truncate(
                SearchProviderHttp.GetString(item, "description") ?? Metadata(item, "description"),
                SearchProviderHttp.MaxSnippetChars, out var snippetTruncated);

            string? content = null;
            var contentTruncated = false;
            if (request.IncludeContent)
                content = SearchProviderHttp.Truncate(
                    SearchProviderHttp.GetString(item, "markdown"), SearchProviderHttp.MaxContentChars, out contentTruncated);

            results.Add(new SearchResultItem(
                SearchProviderHttp.GetString(item, "title") ?? Metadata(item, "title") ?? "",
                SearchProviderHttp.GetString(item, "url") ?? Metadata(item, "sourceURL") ?? Metadata(item, "url") ?? "",
                snippet,
                content,
                null,
                null,
                snippetTruncated || contentTruncated));
        }

        return new SearchResponse(Type, results);
    }

    public async Task<ExtractResponse> ExtractAsync(SearchProviderConfig config, string apiKey, ExtractRequest request, HttpClient client, CancellationToken ct)
    {
        var pages = new List<ExtractedPage>();
        var failures = new List<ExtractFailure>();

        foreach (var url in request.Urls)
        {
            var payload = new JsonObject
            {
                ["url"] = url,
                ["formats"] = MarkdownFormats(),
            };

            using var message = new HttpRequestMessage(HttpMethod.Post, SearchProviderHttp.BuildUri(config.BaseUri, "v2/scrape"))
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            (bool Success, HttpStatusCode StatusCode, string Body, TimeSpan? RetryAfter) result;
            try
            {
                result = await SearchProviderHttp.SendRawAsync(client, message, _timeout, _maxResponseBytes, ct);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested && pages.Count > 0)
            {
                // Pembatalan pemanggil setelah sebagian halaman ditagih: bungkus agar count tetap terbawa,
                // inner OCE dipertahankan supaya hub dapat menahan akuntansi parsial lalu meneruskan pembatalan.
                throw new SearchProviderTransportException(pages.Count, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Timeout/jaringan di tengah loop: halaman yang sudah berhasil tetap tercatat untuk akuntansi biaya parsial.
                throw new SearchProviderTransportException(pages.Count, ex);
            }

            if (!result.Success)
            {
                // Kegagalan tingkat akun/kunci harus memicu transisi status penyedia, bukan dihitung sebagai kegagalan URL.
                if (result.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests
                    || (result.StatusCode == HttpStatusCode.Forbidden && !IsUrlLevelForbidden(result.Body)))
                {
                    throw new SearchProviderException(result.StatusCode, result.Body, result.RetryAfter, null, pages.Count);
                }

                failures.Add(new ExtractFailure(url, $"Gagal mengekstrak halaman (HTTP {(int)result.StatusCode})."));
                continue;
            }

            using var doc = SearchProviderHttp.ParseJson(result.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || SearchProviderHttp.GetString(data, "markdown") is not { } markdown)
            {
                throw new SearchProviderProtocolException("Respons ekstraksi Firecrawl tidak sesuai skema.", pages.Count);
            }

            var text = SearchProviderHttp.Truncate(markdown, SearchProviderHttp.MaxExtractChars, out var truncated);
            pages.Add(new ExtractedPage(url, text, truncated));
        }

        return new ExtractResponse(Type, pages, failures.Count > 0 ? failures : null);
    }

    public async Task<bool> TestAsync(SearchProviderConfig config, string apiKey, HttpClient client, CancellationToken ct)
    {
        await SearchAsync(config, apiKey, new SearchRequest("test", 1), client, ct);
        return true;
    }

    private static JsonArray MarkdownFormats() => new(new JsonObject { ["type"] = "markdown" });

    private static string? Metadata(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("metadata", out var metadata)
            ? SearchProviderHttp.GetString(metadata, property)
            : null;

    /// <summary>403 Firecrawl untuk URL yang tidak didukung/butuh tindakan bersifat per-URL (code terstruktur, bukan substring bebas).</summary>
    private static bool IsUrlLevelForbidden(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var code = SearchProviderHttp.GetString(doc.RootElement, "code");
            return code is "UNSUPPORTED_SITE" or "THIRD_PARTY_DATA_NOT_ENABLED" or "THIRD_PARTY_DATA_ENRICHMENT_NOT_ENABLED"
                or "THIRD_PARTY_DATA_NOT_FOUND" or "THIRD_PARTY_DATA_TERMS_REQUIRED" or "THIRD_PARTY_DATA_UNSUPPORTED_URL"
                or "THIRD_PARTY_DATA_UNSUPPORTED_OPTION";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
