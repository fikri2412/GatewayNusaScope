using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Options;

namespace OperatorAi.Shared.Ai;

/// <summary>
/// Pencarian/ekstraksi lewat gateway (<c>POST /v1/search</c>, <c>POST /v1/extract</c>) memakai autentikasi gateway
/// (<c>Ai:BaseUrl</c>/<c>Ai:ApiKey</c>) — tidak ada kunci penyedia di sisi chatbot. Fallback tanpa kunci hanya untuk
/// 503 dan gateway tidak terjangkau (jaringan/timeout); 401/400/429/500 dan JSON rusak dikembalikan sebagai error
/// yang disanitasi, bukan disamarkan fallback.
/// </summary>
public sealed class GatewayWebSearch(
    HttpClient http,
    IOptions<AiOptions> options,
    FallbackWebSearch fallback,
    ILogger<GatewayWebSearch>? logger = null) : IWebSearch
{
    private const int MaxSearchResponseBytes = 256 * 1024, MaxExtractResponseBytes = 1024 * 1024;

    public async Task<WebSearchResult> SearchAsync(string query, int count, CancellationToken ct)
    {
        var clampedCount = Math.Clamp(count, 1, 10);

        var payload = new JsonObject
        {
            ["query"] = query,
            ["maxResults"] = clampedCount,
            ["includeContent"] = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint("search"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);

        string json;
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                logger?.LogWarning("Gateway search tidak tersedia (HTTP 503); memakai FallbackWebSearch.");
                return await fallback.SearchAsync(query, count, ct);
            }
            if (!res.IsSuccessStatusCode)
            {
                logger?.LogWarning("Gateway search gagal dengan HTTP {StatusCode}; tanpa fallback.", (int)res.StatusCode);
                return new WebSearchResult("gateway", [], $"Pencarian web gagal (HTTP {(int)res.StatusCode}).");
            }
            json = await BoundedContent.ReadAsync(res.Content, MaxSearchResponseBytes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger?.LogWarning("Gateway search tidak terjangkau; memakai FallbackWebSearch.");
            return await fallback.SearchAsync(query, count, ct);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var provider = doc.RootElement.TryGetProperty("provider", out var p) ? p.GetString() ?? "gateway" : "gateway";
            var items = new List<WebSearchResultItem>();
            if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (items.Count >= clampedCount) break;
                    items.Add(new WebSearchResultItem(Str(item, "title"), Str(item, "url"), Str(item, "snippet")));
                }
            }
            return new WebSearchResult(provider, items);
        }
        catch (JsonException)
        {
            logger?.LogWarning("Gateway search mengembalikan JSON yang tidak valid; tanpa fallback.");
            return new WebSearchResult("gateway", [], "Respons pencarian web tidak valid.");
        }
    }

    public async Task<WebExtractResult> ExtractAsync(string url, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["urls"] = new JsonArray(url)
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint("extract"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);

        string json;
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                logger?.LogWarning("Gateway ekstraksi tidak tersedia (HTTP 503); memakai FallbackWebSearch.");
                return await fallback.ExtractAsync(url, ct);
            }
            if (!res.IsSuccessStatusCode)
            {
                logger?.LogWarning("Gateway ekstraksi gagal dengan HTTP {StatusCode}; tanpa fallback.", (int)res.StatusCode);
                return new WebExtractResult("gateway", url, "", $"Pengambilan isi URL gagal (HTTP {(int)res.StatusCode}).");
            }
            json = await BoundedContent.ReadAsync(res.Content, MaxExtractResponseBytes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger?.LogWarning("Gateway ekstraksi tidak terjangkau; memakai FallbackWebSearch.");
            return await fallback.ExtractAsync(url, ct);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var provider = doc.RootElement.TryGetProperty("provider", out var p) ? p.GetString() ?? "gateway" : "gateway";
            if (doc.RootElement.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var page in pages.EnumerateArray())
                {
                    var pageUrl = page.TryGetProperty("url", out var u) ? u.GetString() ?? url : url;
                    return new WebExtractResult(provider, pageUrl, Str(page, "markdown"));
                }
            }
            return new WebExtractResult(provider, url, "", "Isi halaman tidak tersedia.");
        }
        catch (JsonException)
        {
            logger?.LogWarning("Gateway ekstraksi mengembalikan JSON yang tidak valid; tanpa fallback.");
            return new WebExtractResult("gateway", url, "", "Respons pengambilan isi URL tidak valid.");
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private Uri ResolveEndpoint(string relativePath)
    {
        var baseStr = options.Value.BaseUrl.TrimEnd('/') + "/";
        var uri = new Uri(baseStr);
        if (!uri.AbsolutePath.EndsWith("/v1/") && !uri.AbsolutePath.Contains("/v1/"))
        {
            return new Uri(uri, "v1/" + relativePath);
        }
        return new Uri(uri, relativePath);
    }
}
