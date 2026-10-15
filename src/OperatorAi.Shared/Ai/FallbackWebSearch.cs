using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OperatorAi.Shared.Ai;

/// <summary>
/// Pencarian web tanpa kunci (resort terakhir bila gateway 503/tidak terjangkau). Sumbernya hanya jawaban instan
/// DuckDuckGo — bukan hasil pencarian umum — jadi nol hasil dikembalikan sebagai daftar kosong yang jujur, bukan
/// hasil karangan. Ekstraksi halaman langsung dibatasi guard alamat publik (<see cref="PublicUrlGuard"/>) di
/// lapisan koneksi, batas waktu klien, dan potongan ukuran.
/// </summary>
public sealed class FallbackWebSearch(HttpClient? httpClient = null, ILogger<FallbackWebSearch>? logger = null) : IWebSearch
{
    private const int MaxSearchResponseBytes = 128 * 1024, MaxExtractHtmlBytes = 512 * 1024, MaxExtractChars = 4000;
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<WebSearchResult> SearchAsync(string query, int count, CancellationToken ct)
    {
        var clamped = Math.Clamp(count, 1, 5);
        try
        {
            var endpoint = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return Empty();

            var json = await BoundedContent.ReadAsync(res.Content, MaxSearchResponseBytes, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var list = new List<WebSearchResultItem>();

            var heading = root.TryGetProperty("Heading", out var h) ? h.GetString() : null;
            var abstractText = root.TryGetProperty("AbstractText", out var a) ? a.GetString() : null;
            var abstractUrl = root.TryGetProperty("AbstractURL", out var u) ? u.GetString() : null;
            if (!string.IsNullOrWhiteSpace(abstractText))
                list.Add(new WebSearchResultItem(heading ?? query, abstractUrl ?? "", abstractText));

            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in topics.EnumerateArray())
                {
                    if (list.Count >= clamped) break;
                    if (item.TryGetProperty("Text", out var t) && item.TryGetProperty("FirstURL", out var fu))
                    {
                        var text = t.GetString() ?? "";
                        var url = fu.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                            list.Add(new WebSearchResultItem(text.Length > 50 ? text[..50] + "..." : text, url, text));
                    }
                }
            }

            return new WebSearchResult("duckduckgo_fallback", list);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger?.LogWarning("Pencarian fallback tanpa kunci gagal.");
            return Empty();
        }
    }

    public async Task<WebExtractResult> ExtractAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new WebExtractResult("fallback_fetch", url, "", "URL harus diawali http:// atau https://.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return ExtractFailed(url);

            var html = await BoundedContent.ReadAsync(res.Content, MaxExtractHtmlBytes, ct);
            var text = Regex.Replace(html, "<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length == 0) return ExtractFailed(url);
            return new WebExtractResult("fallback_fetch", url, text.Length > MaxExtractChars ? text[..MaxExtractChars] + "..." : text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger?.LogWarning("Pengambilan halaman fallback gagal.");
            return ExtractFailed(url);
        }
    }

    private static WebSearchResult Empty() => new("duckduckgo_fallback", []);

    private static WebExtractResult ExtractFailed(string url) => new("fallback_fetch", url, "", "Gagal mengambil isi URL.");
}
