using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.WebSearch;

/// <summary>
/// Tavily Search/Extract. Dokumen resmi: https://docs.tavily.com/documentation/api-reference/endpoint/search
/// dan https://docs.tavily.com/documentation/api-credits (search basic 1 kredit; extract basic 1 kredit/5 URL berhasil).
/// </summary>
public sealed class TavilySearchAdapter(IOptions<WebSearchOptions>? options = null) : ISearchProvider
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(1,
        options?.Value.ProviderTimeoutSeconds ?? WebSearchOptions.DefaultProviderTimeoutSeconds));
    private readonly int _maxResponseBytes = Math.Max(1024,
        options?.Value.ProviderMaxResponseBytes ?? WebSearchOptions.DefaultMaxResponseBytes);

    public string Type => SearchProviderTypes.Tavily;
    public SearchCapabilities Capabilities => SearchCapabilities.All;
    public string DefaultBaseUri => "https://api.tavily.com";

    /// <summary>Basic search = 1 kredit per permintaan (advanced 2, tidak dipakai); include_raw_content tidak menambah kredit.</summary>
    public decimal EstimateSearchCost(SearchProviderConfig config, SearchRequest request, int resultCount) =>
        config.CostPerSearch;

    /// <summary>Basic extract = 1 kredit per 5 URL berhasil (dibulatkan ke atas); URL gagal tidak ditagih.</summary>
    public decimal EstimateExtractCost(SearchProviderConfig config, ExtractRequest request, int successCount) =>
        config.CostPerExtract * Math.Ceiling(Math.Max(0, successCount) / 5m);

    public async Task<SearchResponse> SearchAsync(SearchProviderConfig config, string apiKey, SearchRequest request, HttpClient client, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["query"] = request.Query,
            ["max_results"] = Math.Clamp(request.MaxResults, 1, 20),
            ["search_depth"] = "basic",
            ["include_raw_content"] = request.IncludeContent,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, SearchProviderHttp.BuildUri(config.BaseUri, "search"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var doc = await SearchProviderHttp.SendJsonAsync(client, message, _timeout, _maxResponseBytes, ct);
        if (!SearchProviderHttp.TryGetArray(doc.RootElement, "results", out var items))
            throw new SearchProviderProtocolException("Respons pencarian Tavily tidak berisi daftar hasil.");

        var results = new List<SearchResultItem>();
        foreach (var item in items.EnumerateArray())
        {
            var snippet = SearchProviderHttp.Truncate(
                SearchProviderHttp.GetString(item, "content"), SearchProviderHttp.MaxSnippetChars, out var snippetTruncated);

            string? content = null;
            var contentTruncated = false;
            if (request.IncludeContent)
                content = SearchProviderHttp.Truncate(
                    SearchProviderHttp.GetString(item, "raw_content"), SearchProviderHttp.MaxContentChars, out contentTruncated);

            double? score = item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("score", out var scoreElement)
                && scoreElement.TryGetDouble(out var scoreValue)
                    ? scoreValue
                    : null;

            results.Add(new SearchResultItem(
                SearchProviderHttp.GetString(item, "title") ?? "",
                SearchProviderHttp.GetString(item, "url") ?? "",
                snippet,
                content,
                score,
                SearchProviderHttp.GetString(item, "published_date"),
                snippetTruncated || contentTruncated));
        }

        return new SearchResponse(Type, results);
    }

    public async Task<ExtractResponse> ExtractAsync(SearchProviderConfig config, string apiKey, ExtractRequest request, HttpClient client, CancellationToken ct)
    {
        var urls = new JsonArray();
        foreach (var url in request.Urls) urls.Add(url);

        var payload = new JsonObject
        {
            ["urls"] = urls,
            ["format"] = "markdown",
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, SearchProviderHttp.BuildUri(config.BaseUri, "extract"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var doc = await SearchProviderHttp.SendJsonAsync(client, message, _timeout, _maxResponseBytes, ct);
        var root = doc.RootElement;
        var hasResults = SearchProviderHttp.TryGetArray(root, "results", out var items);
        var hasFailures = SearchProviderHttp.TryGetArray(root, "failed_results", out var failedItems);
        if (!hasResults && !hasFailures)
            throw new SearchProviderProtocolException("Respons ekstraksi Tavily tidak berisi hasil.");

        var pages = new List<ExtractedPage>();
        if (hasResults)
        {
            foreach (var item in items.EnumerateArray())
            {
                var markdown = SearchProviderHttp.Truncate(
                    SearchProviderHttp.GetString(item, "raw_content"), SearchProviderHttp.MaxExtractChars, out var truncated);
                pages.Add(new ExtractedPage(SearchProviderHttp.GetString(item, "url") ?? "", markdown, truncated));
            }
        }

        var failures = new List<ExtractFailure>();
        if (hasFailures)
        {
            foreach (var item in failedItems.EnumerateArray())
                failures.Add(new ExtractFailure(SearchProviderHttp.GetString(item, "url") ?? "", "Gagal mengekstrak halaman."));
        }

        return new ExtractResponse(Type, pages, failures.Count > 0 ? failures : null);
    }

    public async Task<bool> TestAsync(SearchProviderConfig config, string apiKey, HttpClient client, CancellationToken ct)
    {
        await SearchAsync(config, apiKey, new SearchRequest("test", 1), client, ct);
        return true;
    }
}
