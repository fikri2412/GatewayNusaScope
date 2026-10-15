using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.WebSearch;

/// <summary>
/// Exa Search/Contents. Dokumen resmi: https://exa.ai/docs/reference/search dan https://exa.ai/docs/reference/get-contents.
/// Snippet diambil dari highlights (retrieval terhitung, termasuk dalam harga dasar hingga 10 hasil per pencarian).
/// </summary>
public sealed class ExaSearchAdapter(IOptions<WebSearchOptions>? options = null) : ISearchProvider
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(1,
        options?.Value.ProviderTimeoutSeconds ?? WebSearchOptions.DefaultProviderTimeoutSeconds));
    private readonly int _maxResponseBytes = Math.Max(1024,
        options?.Value.ProviderMaxResponseBytes ?? WebSearchOptions.DefaultMaxResponseBytes);

    public string Type => SearchProviderTypes.Exa;
    public SearchCapabilities Capabilities => SearchCapabilities.All;
    public string DefaultBaseUri => "https://api.exa.ai";

    /// <summary>1 unit per permintaan; konten untuk 10 hasil pertama termasuk, halaman berikutnya $1/1k (1 unit/halaman).</summary>
    public decimal EstimateSearchCost(SearchProviderConfig config, SearchRequest request, int resultCount) =>
        config.CostPerSearch * (1m + Math.Max(0, resultCount - 10));

    /// <summary>1 unit per halaman berhasil ($1/1k halaman).</summary>
    public decimal EstimateExtractCost(SearchProviderConfig config, ExtractRequest request, int successCount) =>
        config.CostPerExtract * Math.Max(0, successCount);

    public async Task<SearchResponse> SearchAsync(SearchProviderConfig config, string apiKey, SearchRequest request, HttpClient client, CancellationToken ct)
    {
        var contents = new JsonObject { ["highlights"] = true };
        if (request.IncludeContent)
            contents["text"] = new JsonObject { ["maxCharacters"] = SearchProviderHttp.MaxContentChars };

        var payload = new JsonObject
        {
            ["query"] = request.Query,
            ["numResults"] = Math.Clamp(request.MaxResults, 1, 100),
            ["type"] = "auto",
            ["contents"] = contents,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, SearchProviderHttp.BuildUri(config.BaseUri, "search"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Add("x-api-key", apiKey);

        using var doc = await SearchProviderHttp.SendJsonAsync(client, message, _timeout, _maxResponseBytes, ct);
        if (!SearchProviderHttp.TryGetArray(doc.RootElement, "results", out var items))
            throw new SearchProviderProtocolException("Respons pencarian Exa tidak berisi daftar hasil.");

        var results = new List<SearchResultItem>();
        foreach (var item in items.EnumerateArray())
        {
            var snippet = SearchProviderHttp.Truncate(
                JoinHighlights(item), SearchProviderHttp.MaxSnippetChars, out var snippetTruncated);

            string? content = null;
            var contentTruncated = false;
            if (request.IncludeContent)
                content = SearchProviderHttp.Truncate(
                    SearchProviderHttp.GetString(item, "text"), SearchProviderHttp.MaxContentChars, out contentTruncated);

            results.Add(new SearchResultItem(
                SearchProviderHttp.GetString(item, "title") ?? "",
                SearchProviderHttp.GetString(item, "url") ?? "",
                snippet,
                content,
                FirstHighlightScore(item),
                SearchProviderHttp.GetString(item, "publishedDate"),
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
            ["text"] = new JsonObject { ["maxCharacters"] = SearchProviderHttp.MaxExtractChars },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, SearchProviderHttp.BuildUri(config.BaseUri, "contents"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Add("x-api-key", apiKey);

        using var doc = await SearchProviderHttp.SendJsonAsync(client, message, _timeout, _maxResponseBytes, ct);
        var root = doc.RootElement;
        if (!SearchProviderHttp.TryGetArray(root, "results", out var items))
            throw new SearchProviderProtocolException("Respons ekstraksi Exa tidak berisi daftar hasil.");

        var pages = new List<ExtractedPage>();
        var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var url = SearchProviderHttp.GetString(item, "url") ?? "";
            var text = SearchProviderHttp.Truncate(
                SearchProviderHttp.GetString(item, "text"), SearchProviderHttp.MaxExtractChars, out var truncated);
            pages.Add(new ExtractedPage(url, text, truncated));
            returned.Add(url);
        }

        var statusErrors = CollectStatusErrors(root);

        var failures = new List<ExtractFailure>();
        foreach (var requested in request.Urls)
        {
            if (returned.Contains(requested)) continue;
            failures.Add(new ExtractFailure(
                requested,
                statusErrors.TryGetValue(requested, out var errorMessage) ? errorMessage : "Gagal mengekstrak halaman."));
        }

        return new ExtractResponse(Type, pages, failures.Count > 0 ? failures : null);
    }

    public async Task<bool> TestAsync(SearchProviderConfig config, string apiKey, HttpClient client, CancellationToken ct)
    {
        await SearchAsync(config, apiKey, new SearchRequest("test", 1), client, ct);
        return true;
    }

    private static string JoinHighlights(JsonElement item)
    {
        if (!SearchProviderHttp.TryGetArray(item, "highlights", out var highlights)) return "";
        var parts = new List<string>();
        foreach (var highlight in highlights.EnumerateArray())
        {
            if (highlight.ValueKind != JsonValueKind.String) continue;
            var text = highlight.GetString();
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }
        return string.Join(" … ", parts);
    }

    private static double? FirstHighlightScore(JsonElement item)
    {
        if (!SearchProviderHttp.TryGetArray(item, "highlightScores", out var scores)) return null;
        foreach (var score in scores.EnumerateArray())
            if (score.TryGetDouble(out var value)) return value;
        return null;
    }

    private static Dictionary<string, string> CollectStatusErrors(JsonElement root)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!SearchProviderHttp.TryGetArray(root, "statuses", out var statuses)) return errors;

        foreach (var status in statuses.EnumerateArray())
        {
            var id = SearchProviderHttp.GetString(status, "id");
            if (id == null || SearchProviderHttp.GetString(status, "status") != "error") continue;

            var code = status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("httpStatusCode", out var httpCode)
                && httpCode.TryGetInt32(out var statusCode)
                    ? $" (HTTP {statusCode})"
                    : "";
            errors[id] = $"Gagal mengekstrak halaman{code}.";
        }

        return errors;
    }
}
