using System.Net;
using System.Text;
using System.Text.Json;
using AiGateway.Core.Domain;
using AiGateway.Core.WebSearch;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace AiGateway.Tests;

/// <summary>
/// Kontrak adapter terhadap dokumentasi resmi penyedia: payload termurah, kode error resmi,
/// batas output ternormalisasi, dan satuan biaya. Tanpa database.
/// </summary>
public sealed class WebSearchAdapterTests
{
    private static SearchProviderConfig Config(string type, string baseUri) => new()
    {
        Name = type,
        Type = type,
        BaseUri = baseUri,
        ApiKeyEncrypted = "enc",
        KeyHint = "****",
    };

    private sealed class MockHttp(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Sent.Add((request, body));
            return Task.FromResult(handler(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static JsonElement Payload(MockHttp mock, int index = 0) =>
        JsonDocument.Parse(mock.Sent[index].Body!).RootElement;

    [Fact]
    public async Task Tavily_search_sends_documented_basic_payload_and_maps_results()
    {
        var mock = new MockHttp(_ => Json("""
            {"results":[{"title":"NusaScope Docs","url":"https://example.com/docs","content":"Short summary","score":0.98,"published_date":"2026-10-14"}]}
            """));
        using var client = new HttpClient(mock);
        var adapter = new TavilySearchAdapter();

        var response = await adapter.SearchAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "test-tavily-key",
            new SearchRequest("test query", 50, false), client, default);

        var (request, _) = mock.Sent[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.tavily.com/search", request.RequestUri!.ToString());
        Assert.Equal("Bearer test-tavily-key", request.Headers.Authorization!.ToString());

        var payload = Payload(mock);
        Assert.Equal("test query", payload.GetProperty("query").GetString());
        Assert.Equal(20, payload.GetProperty("max_results").GetInt32()); // batas resmi 1..20
        Assert.Equal("basic", payload.GetProperty("search_depth").GetString()); // 1 kredit
        Assert.False(payload.GetProperty("include_raw_content").GetBoolean());
        Assert.False(payload.TryGetProperty("api_key", out _)); // kunci hanya di header

        var r = Assert.Single(response.Results);
        Assert.Equal("NusaScope Docs", r.Title);
        Assert.Equal("https://example.com/docs", r.Url);
        Assert.Equal("Short summary", r.Snippet);
        Assert.Equal(0.98, r.Score);
        Assert.Equal("2026-10-14", r.PublishedDate);
        Assert.Null(r.Content);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Tavily_search_include_content_requests_raw_content_and_marks_truncation()
    {
        var raw = new string('x', 25_000);
        var mock = new MockHttp(_ => Json(JsonSerializer.Serialize(new
        {
            results = new[] { new { title = "t", url = "https://example.com", content = "s", raw_content = raw } }
        })));
        using var client = new HttpClient(mock);

        var response = await new TavilySearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key",
            new SearchRequest("q", 5, true), client, default);

        Assert.True(Payload(mock).GetProperty("include_raw_content").GetBoolean());
        var r = Assert.Single(response.Results);
        Assert.Equal(20_000, r.Content!.Length);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task Tavily_search_truncates_long_snippets_and_marks_them()
    {
        var content = new string('y', 2_000);
        var mock = new MockHttp(_ => Json(JsonSerializer.Serialize(new
        {
            results = new[] { new { title = "t", url = "https://example.com", content } }
        })));
        using var client = new HttpClient(mock);

        var response = await new TavilySearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key",
            new SearchRequest("q", 1), client, default);

        var r = Assert.Single(response.Results);
        Assert.Equal(1_000, r.Snippet.Length);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task Tavily_extract_maps_partial_success_and_records_failures()
    {
        var mock = new MockHttp(_ => Json("""
            {"results":[{"url":"https://a.example/1","raw_content":"# A"}],
             "failed_results":[{"url":"https://a.example/2","error":"Failed to retrieve content"}]}
            """));
        using var client = new HttpClient(mock);

        var response = await new TavilySearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key",
            new ExtractRequest(["https://a.example/1", "https://a.example/2"]), client, default);

        var payload = Payload(mock);
        Assert.Equal("markdown", payload.GetProperty("format").GetString());
        Assert.Equal("https://api.tavily.com/extract", mock.Sent[0].Request.RequestUri!.ToString());

        var page = Assert.Single(response.Pages);
        Assert.Equal("# A", page.Markdown);
        var failure = Assert.Single(response.Failures!);
        Assert.Equal("https://a.example/2", failure.Url);
        Assert.Equal("Gagal mengekstrak halaman.", failure.Error); // pesan tetap, bukan body penyedia
    }

    [Theory]
    [InlineData(432)]
    [InlineData(433)]
    public async Task Tavily_documented_limit_codes_throw_provider_exception(int code)
    {
        var mock = new MockHttp(_ => Json("""{"detail":{"error":"limit exceeded"}}""", (HttpStatusCode)code));
        using var client = new HttpClient(mock);

        var ex = await Assert.ThrowsAsync<SearchProviderException>(() => new TavilySearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key",
            new SearchRequest("q", 5), client, default));

        Assert.Equal(code, (int)ex.ResponseStatusCode);
    }

    [Fact]
    public async Task Tavily_extract_429_keeps_retry_after_header()
    {
        var response = Json("""{"detail":{"error":"rate limited"}}""", (HttpStatusCode)429);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
        var mock = new MockHttp(_ => response);
        using var client = new HttpClient(mock);

        var ex = await Assert.ThrowsAsync<SearchProviderException>(() => new TavilySearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key",
            new ExtractRequest(["https://a.example/1"]), client, default));

        Assert.Equal(TimeSpan.FromSeconds(45), ex.RetryAfter);
    }

    [Theory]
    [InlineData("bukan json")]
    [InlineData("""{"answer":"tanpa results"}""")]
    public async Task Tavily_malformed_2xx_throws_protocol_exception(string body)
    {
        var mock = new MockHttp(_ => Json(body));
        using var client = new HttpClient(mock);

        await Assert.ThrowsAsync<SearchProviderProtocolException>(() => new TavilySearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key",
            new SearchRequest("q", 5), client, default));
    }

    [Fact]
    public async Task Adapter_bounds_response_body_bytes_and_rejects_cut_payload()
    {
        var mock = new MockHttp(_ => Json("{\"results\":[{\"title\":\"" + new string('x', 5_000) + "\"}]}"));
        using var client = new HttpClient(mock);
        var adapter = new TavilySearchAdapter(Options.Create(new WebSearchOptions { ProviderMaxResponseBytes = 1_024 }));

        await Assert.ThrowsAsync<SearchProviderProtocolException>(() => adapter.SearchAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key",
            new SearchRequest("q", 5), client, default));
    }

    [Fact]
    public async Task Exa_search_requests_highlights_as_cheapest_snippet_source()
    {
        var mock = new MockHttp(_ => Json("""
            {"results":[{"title":"Exa Result","url":"https://exa.ai/item","highlights":["satu","dua"],
             "highlightScores":[0.8],"publishedDate":"2026-10-10"}]}
            """));
        using var client = new HttpClient(mock);

        var response = await new ExaSearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Exa, "https://api.exa.ai"), "test-exa-key",
            new SearchRequest("ai models", 500, false), client, default);

        var (request, _) = mock.Sent[0];
        Assert.Equal("https://api.exa.ai/search", request.RequestUri!.ToString());
        Assert.Equal("test-exa-key", request.Headers.GetValues("x-api-key").First());

        var payload = Payload(mock);
        Assert.Equal(100, payload.GetProperty("numResults").GetInt32()); // batas resmi 1..100
        Assert.Equal("auto", payload.GetProperty("type").GetString());
        Assert.True(payload.GetProperty("contents").GetProperty("highlights").GetBoolean());
        Assert.False(payload.GetProperty("contents").TryGetProperty("text", out _)); // teks penuh hanya bila diminta

        var r = Assert.Single(response.Results);
        Assert.Equal("satu … dua", r.Snippet);
        Assert.Equal(0.8, r.Score);
        Assert.Equal("2026-10-10", r.PublishedDate);
        Assert.Null(r.Content);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Exa_search_include_content_requests_bounded_text()
    {
        var text = new string('z', 25_000);
        var mock = new MockHttp(_ => Json(JsonSerializer.Serialize(new
        {
            results = new[] { new { title = "t", url = "https://exa.ai/x", highlights = new[] { "s" }, text } }
        })));
        using var client = new HttpClient(mock);

        var response = await new ExaSearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Exa, "https://api.exa.ai"), "key",
            new SearchRequest("q", 5, true), client, default);

        var contents = Payload(mock).GetProperty("contents");
        Assert.Equal(20_000, contents.GetProperty("text").GetProperty("maxCharacters").GetInt32());
        Assert.True(contents.GetProperty("highlights").GetBoolean());

        var r = Assert.Single(response.Results);
        Assert.Equal(20_000, r.Content!.Length);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task Exa_extract_reports_status_errors_as_partial_failures()
    {
        var mock = new MockHttp(_ => Json("""
            {"results":[{"title":"p","url":"https://a.example/1","text":"konten satu"}],
             "statuses":[
               {"id":"https://a.example/1","status":"success","source":"cached"},
               {"id":"https://a.example/2","status":"error","error":{"tag":"CRAWL_ERROR","httpStatusCode":404}}]}
            """));
        using var client = new HttpClient(mock);

        var response = await new ExaSearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Exa, "https://api.exa.ai"), "key",
            new ExtractRequest(["https://a.example/1", "https://a.example/2"]), client, default);

        Assert.Equal("https://api.exa.ai/contents", mock.Sent[0].Request.RequestUri!.ToString());
        var payload = Payload(mock);
        Assert.Equal(100_000, payload.GetProperty("text").GetProperty("maxCharacters").GetInt32());

        var page = Assert.Single(response.Pages);
        Assert.Equal("konten satu", page.Markdown);
        var failure = Assert.Single(response.Failures!);
        Assert.Equal("https://a.example/2", failure.Url);
        Assert.Equal("Gagal mengekstrak halaman (HTTP 404).", failure.Error);
    }

    [Fact]
    public async Task Exa_extract_missing_result_without_status_is_recorded_as_failure()
    {
        var mock = new MockHttp(_ => Json("""{"results":[]}"""));
        using var client = new HttpClient(mock);

        var response = await new ExaSearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Exa, "https://api.exa.ai"), "key",
            new ExtractRequest(["https://a.example/1"]), client, default);

        Assert.Empty(response.Pages);
        var failure = Assert.Single(response.Failures!);
        Assert.Equal("Gagal mengekstrak halaman.", failure.Error);
    }

    [Fact]
    public async Task Exa_payment_required_throws_provider_exception()
    {
        var mock = new MockHttp(_ => Json("""{"requestId":"r","error":"out of credits","tag":"NO_CREDITS"}""", HttpStatusCode.PaymentRequired));
        using var client = new HttpClient(mock);

        var ex = await Assert.ThrowsAsync<SearchProviderException>(() => new ExaSearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Exa, "https://api.exa.ai"), "key",
            new SearchRequest("q", 5), client, default));

        Assert.Equal(HttpStatusCode.PaymentRequired, ex.ResponseStatusCode);
    }

    [Fact]
    public async Task Firecrawl_search_uses_v2_endpoint_and_web_results_without_scrape()
    {
        var mock = new MockHttp(_ => Json("""
            {"success":true,"data":{"web":[{"title":"Firecrawl Scrape","url":"https://firecrawl.dev/item","description":"Snippet description"}]},"creditsUsed":2}
            """));
        using var client = new HttpClient(mock);

        var response = await new FirecrawlSearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "test-firecrawl-key",
            new SearchRequest("web scrape", 250, false), client, default);

        var (request, _) = mock.Sent[0];
        Assert.Equal("https://api.firecrawl.dev/v2/search", request.RequestUri!.ToString());
        Assert.Equal("Bearer test-firecrawl-key", request.Headers.Authorization!.ToString());

        var payload = Payload(mock);
        Assert.Equal(100, payload.GetProperty("limit").GetInt32()); // batas resmi 1..100
        Assert.False(payload.TryGetProperty("scrapeOptions", out _)); // tanpa konten = paling murah

        var r = Assert.Single(response.Results);
        Assert.Equal("Firecrawl Scrape", r.Title);
        Assert.Equal("Snippet description", r.Snippet);
        Assert.Null(r.Content);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Firecrawl_search_include_content_requests_v2_markdown_format()
    {
        var markdown = new string('m', 25_000);
        var mock = new MockHttp(_ => Json(JsonSerializer.Serialize(new
        {
            success = true,
            data = new { web = new[] { new { title = "t", url = "https://firecrawl.dev/x", description = "d", markdown } } }
        })));
        using var client = new HttpClient(mock);

        var response = await new FirecrawlSearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new SearchRequest("q", 5, true), client, default);

        var formats = Payload(mock).GetProperty("scrapeOptions").GetProperty("formats");
        Assert.Equal("markdown", formats[0].GetProperty("type").GetString()); // v2: objek, bukan string polos

        var r = Assert.Single(response.Results);
        Assert.Equal(20_000, r.Content!.Length);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task Firecrawl_search_maps_metadata_title_fallback()
    {
        var mock = new MockHttp(_ => Json("""
            {"success":true,"data":{"web":[{"url":"https://firecrawl.dev/x","metadata":{"title":"Judul Metadata","sourceURL":"https://firecrawl.dev/x"}}]}}
            """));
        using var client = new HttpClient(mock);

        var response = await new FirecrawlSearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new SearchRequest("q", 1), client, default);

        var r = Assert.Single(response.Results);
        Assert.Equal("Judul Metadata", r.Title);
        Assert.Equal("https://firecrawl.dev/x", r.Url);
    }

    [Fact]
    public async Task Firecrawl_2xx_with_success_false_throws_protocol_exception()
    {
        var mock = new MockHttp(_ => Json("""{"success":false,"error":"unexpected"}"""));
        using var client = new HttpClient(mock);

        await Assert.ThrowsAsync<SearchProviderProtocolException>(() => new FirecrawlSearchAdapter().SearchAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new SearchRequest("q", 5), client, default));
    }

    [Fact]
    public async Task Firecrawl_extract_records_per_url_failures_and_keeps_partial_pages()
    {
        var mock = new MockHttp(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("/ok")
                ? Json("""{"success":true,"data":{"markdown":"# Page"}}""")
                : Json("""{"success":false,"error":"Not Found"}""", HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(mock);

        var response = await new FirecrawlSearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new ExtractRequest(["https://a.example/ok", "https://a.example/gone"]), client, default);

        Assert.Equal(2, mock.Sent.Count);
        Assert.All(mock.Sent, s => Assert.Equal("https://api.firecrawl.dev/v2/scrape", s.Request.RequestUri!.ToString()));
        var formats = Payload(mock).GetProperty("formats");
        Assert.Equal("markdown", formats[0].GetProperty("type").GetString());

        var page = Assert.Single(response.Pages);
        Assert.Equal("https://a.example/ok", page.Url);
        Assert.Equal("# Page", page.Markdown);

        var failure = Assert.Single(response.Failures!);
        Assert.Equal("https://a.example/gone", failure.Url);
        Assert.Equal("Gagal mengekstrak halaman (HTTP 404).", failure.Error);
    }

    [Fact]
    public async Task Firecrawl_extract_unsupported_site_403_is_url_failure_not_provider_failure()
    {
        var mock = new MockHttp(_ => Json(
            """{"success":false,"error":"We apologize for the inconvenience but we do not support this site","code":"UNSUPPORTED_SITE"}""",
            HttpStatusCode.Forbidden));
        using var client = new HttpClient(mock);

        var response = await new FirecrawlSearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new ExtractRequest(["https://unsupported.example/x"]), client, default);

        Assert.Empty(response.Pages);
        var failure = Assert.Single(response.Failures!);
        Assert.Equal("Gagal mengekstrak halaman (HTTP 403).", failure.Error);
    }

    [Fact]
    public async Task Firecrawl_extract_generic_403_throws_provider_exception()
    {
        var mock = new MockHttp(_ => Json("""{"success":false,"error":"Forbidden"}""", HttpStatusCode.Forbidden));
        using var client = new HttpClient(mock);

        var ex = await Assert.ThrowsAsync<SearchProviderException>(() => new FirecrawlSearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new ExtractRequest(["https://a.example/1"]), client, default));

        Assert.Equal(HttpStatusCode.Forbidden, ex.ResponseStatusCode);
    }

    [Fact]
    public async Task Firecrawl_extract_payment_required_throws_provider_exception()
    {
        var mock = new MockHttp(_ => Json("""{"success":false,"error":"Payment Required: Insufficient credits"}""", HttpStatusCode.PaymentRequired));
        using var client = new HttpClient(mock);

        var ex = await Assert.ThrowsAsync<SearchProviderException>(() => new FirecrawlSearchAdapter().ExtractAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new ExtractRequest(["https://a.example/1"]), client, default));

        Assert.Equal(HttpStatusCode.PaymentRequired, ex.ResponseStatusCode);
    }

    [Theory]
    [InlineData(SearchProviderTypes.Tavily, 432, SearchErrorType.Exhausted)]
    [InlineData(SearchProviderTypes.Tavily, 433, SearchErrorType.Exhausted)]
    [InlineData(SearchProviderTypes.Tavily, 401, SearchErrorType.Broken)]
    [InlineData(SearchProviderTypes.Tavily, 429, SearchErrorType.RateLimited)]
    [InlineData(SearchProviderTypes.Exa, 402, SearchErrorType.Exhausted)]
    [InlineData(SearchProviderTypes.Exa, 403, SearchErrorType.Broken)]
    [InlineData(SearchProviderTypes.Exa, 429, SearchErrorType.RateLimited)]
    [InlineData(SearchProviderTypes.Firecrawl, 402, SearchErrorType.Exhausted)]
    [InlineData(SearchProviderTypes.Firecrawl, 401, SearchErrorType.Broken)]
    [InlineData(SearchProviderTypes.Firecrawl, 404, SearchErrorType.ClientError)]
    [InlineData(SearchProviderTypes.Tavily, 500, SearchErrorType.RetryableServer)]
    public void Classifier_maps_documented_status_codes(string type, int code, SearchErrorType expected)
    {
        var classified = SearchErrorClassifier.Classify(type, (HttpStatusCode)code, "body", null, null, null);
        Assert.Equal(expected, classified.ErrorType);
    }

    [Fact]
    public void Classifier_defaults_never_match_body_substrings()
    {
        // Body berisi kata "credits/quota" TIDAK boleh mengubah 400 menjadi Exhausted (kode resmi 432/433 saja).
        var classified = SearchErrorClassifier.Classify(
            SearchProviderTypes.Tavily, HttpStatusCode.BadRequest, "insufficient credits quota exceeded plan limit", null, null, null);

        Assert.Equal(SearchErrorType.ClientError, classified.ErrorType);
        Assert.DoesNotContain("credits", classified.SanitizedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quota", classified.SanitizedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan limit", classified.SanitizedMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classifier_override_rules_are_explicit_and_bounded()
    {
        const string rules = """{"exhaustedStatusCodes":[418],"exhaustedBodySubstrings":["custom quota phrase"],"brokenStatusCodes":[451],"rateLimitStatusCodes":[299]}""";

        var byCode = SearchErrorClassifier.Classify(SearchProviderTypes.Tavily, (HttpStatusCode)418, "", null, null, rules);
        Assert.Equal(SearchErrorType.Exhausted, byCode.ErrorType);

        var byBody = SearchErrorClassifier.Classify(SearchProviderTypes.Tavily, HttpStatusCode.BadRequest, "x custom QUOTA phrase y", null, null, rules);
        Assert.Equal(SearchErrorType.Exhausted, byBody.ErrorType);

        var broken = SearchErrorClassifier.Classify(SearchProviderTypes.Tavily, (HttpStatusCode)451, "", null, null, rules);
        Assert.Equal(SearchErrorType.Broken, broken.ErrorType);

        var rate = SearchErrorClassifier.Classify(SearchProviderTypes.Tavily, (HttpStatusCode)299, "", TimeSpan.FromSeconds(7), null, rules);
        Assert.Equal(SearchErrorType.RateLimited, rate.ErrorType);
        Assert.Equal(TimeSpan.FromSeconds(7), rate.RetryAfter);
    }

    [Fact]
    public void Classifier_invalid_override_json_falls_back_to_defaults()
    {
        var classified = SearchErrorClassifier.Classify(SearchProviderTypes.Tavily, (HttpStatusCode)432, "", null, null, "{bukan json");
        Assert.Equal(SearchErrorType.Exhausted, classified.ErrorType);
    }

    [Fact]
    public void Classifier_maps_protocol_timeout_and_network_exceptions_with_fixed_messages()
    {
        var protocol = SearchErrorClassifier.Classify(SearchProviderTypes.Exa, null, null, null,
            new SearchProviderProtocolException("rahasia"), null);
        Assert.Equal(SearchErrorType.RetryableServer, protocol.ErrorType);
        Assert.DoesNotContain("rahasia", protocol.SanitizedMessage);

        var timeout = SearchErrorClassifier.Classify(SearchProviderTypes.Exa, null, null, null, new TaskCanceledException(), null);
        Assert.Equal(SearchErrorType.RetryableServer, timeout.ErrorType);

        var pollyTimeout = SearchErrorClassifier.Classify(SearchProviderTypes.Exa, null, null, null, new TimeoutRejectedException(), null);
        Assert.Equal(SearchErrorType.RetryableServer, pollyTimeout.ErrorType);

        var network = SearchErrorClassifier.Classify(SearchProviderTypes.Exa, null, null, null, new HttpRequestException("host rahasia"), null);
        Assert.Equal(SearchErrorType.RetryableServer, network.ErrorType);
        Assert.DoesNotContain("rahasia", network.SanitizedMessage);
    }

    [Fact]
    public void Adapters_estimate_documented_provider_cost_units()
    {
        var config = Config(SearchProviderTypes.Tavily, "https://api.tavily.com");
        config.CostPerSearch = 3m;
        config.CostPerExtract = 5m;

        var tavily = new TavilySearchAdapter();
        Assert.Equal(3m, tavily.EstimateSearchCost(config, new SearchRequest("q", 10), 10)); // basic = 1 kredit
        Assert.Equal(5m, tavily.EstimateExtractCost(config, new ExtractRequest(["u1", "u2", "u3", "u4", "u5"]), 5)); // 5 URL = 1 kredit
        Assert.Equal(10m, tavily.EstimateExtractCost(config, new ExtractRequest(["u"]), 6)); // dibulatkan ke atas
        Assert.Equal(0m, tavily.EstimateExtractCost(config, new ExtractRequest(["u"]), 0)); // URL gagal tidak ditagih

        var exa = new ExaSearchAdapter();
        Assert.Equal(3m, exa.EstimateSearchCost(config, new SearchRequest("q", 10), 10)); // 1 permintaan
        Assert.Equal(9m, exa.EstimateSearchCost(config, new SearchRequest("q", 12), 12)); // +2 halaman konten
        Assert.Equal(15m, exa.EstimateExtractCost(config, new ExtractRequest(["u1", "u2", "u3"]), 3)); // $1/1k halaman

        var firecrawl = new FirecrawlSearchAdapter();
        Assert.Equal(6m, firecrawl.EstimateSearchCost(config, new SearchRequest("q", 10, false), 10)); // 2 kredit/10 hasil
        Assert.Equal(12m, firecrawl.EstimateSearchCost(config, new SearchRequest("q", 11, false), 11)); // dibulatkan
        Assert.Equal(36m, firecrawl.EstimateSearchCost(config, new SearchRequest("q", 10, true), 10)); // +1 kredit/halaman
        Assert.Equal(10m, firecrawl.EstimateExtractCost(config, new ExtractRequest(["u1", "u2"]), 2)); // 1 kredit/halaman
    }

    [Fact]
    public async Task Adapter_test_calls_stay_cheap()
    {
        var mock = new MockHttp(_ => Json("""{"results":[]}"""));
        using var client = new HttpClient(mock);

        var ok = await new TavilySearchAdapter().TestAsync(
            Config(SearchProviderTypes.Tavily, "https://api.tavily.com"), "key", client, default);

        Assert.True(ok);
        var payload = Payload(mock);
        Assert.Equal(1, payload.GetProperty("max_results").GetInt32());
        Assert.False(payload.GetProperty("include_raw_content").GetBoolean());
    }

    [Fact]
    public async Task Firecrawl_extract_keeps_billed_cost_when_next_url_is_rate_limited()
    {
        var mock = new MockHttp(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("/ok")
                ? Json("""{"success":true,"data":{"markdown":"# Page"}}""")
                : Json("""{"success":false,"error":"Rate limit exceeded"}""", HttpStatusCode.TooManyRequests);
        });
        using var client = new HttpClient(mock);
        var config = Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev");
        config.CostPerExtract = 5m;
        var adapter = new FirecrawlSearchAdapter();
        var request = new ExtractRequest(["https://a.example/ok", "https://a.example/limited"]);

        var ex = await Assert.ThrowsAsync<SearchProviderException>(() => adapter.ExtractAsync(config, "key", request, client, default));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.ResponseStatusCode);
        Assert.Equal(1, ex.SuccessfulPageCount);
        Assert.Equal(5m, adapter.EstimateExtractCost(config, request, ex.SuccessfulPageCount)); // biaya halaman yang sudah ditagih tidak hilang

        var classified = SearchErrorClassifier.Classify(config.Type, ex.ResponseStatusCode, ex.ResponseBody, ex.RetryAfter, ex, null);
        Assert.Equal(SearchErrorType.RateLimited, classified.ErrorType);
    }

    [Fact]
    public async Task Firecrawl_extract_transport_failure_keeps_successful_page_count()
    {
        var mock = new MockHttp(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("/ok")) return Json("""{"success":true,"data":{"markdown":"# Page"}}""");
            throw new TaskCanceledException("deadline");
        });
        using var client = new HttpClient(mock);
        var config = Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev");
        var adapter = new FirecrawlSearchAdapter();

        var ex = await Assert.ThrowsAsync<SearchProviderTransportException>(() => adapter.ExtractAsync(
            config, "key", new ExtractRequest(["https://a.example/ok", "https://a.example/slow"]), client, default));

        Assert.Equal(1, ex.SuccessfulPageCount);
        var classified = SearchErrorClassifier.Classify(config.Type, null, null, null, ex, null);
        Assert.Equal(SearchErrorType.RetryableServer, classified.ErrorType);
        Assert.Contains("batas waktu", classified.SanitizedMessage);
    }

    [Fact]
    public async Task Firecrawl_extract_caller_cancellation_after_billed_page_keeps_count_and_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var mock = new MockHttp(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("/ok")) return Json("""{"success":true,"data":{"markdown":"# Page"}}""");
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = new HttpClient(mock);
        var config = Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev");
        config.CostPerExtract = 3m;
        var adapter = new FirecrawlSearchAdapter();
        var request = new ExtractRequest(["https://a.example/ok", "https://a.example/cancel"]);

        var ex = await Assert.ThrowsAsync<SearchProviderTransportException>(() => adapter.ExtractAsync(config, "key", request, client, cts.Token));

        Assert.Equal(1, ex.SuccessfulPageCount); // halaman pertama sudah ditagih penyedia
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException); // pembatalan tidak hilang
        Assert.Equal(3m, adapter.EstimateExtractCost(config, request, ex.SuccessfulPageCount));
    }

    [Fact]
    public async Task Firecrawl_extract_caller_cancellation_without_billed_pages_propagates_raw()
    {
        using var cts = new CancellationTokenSource();
        var mock = new MockHttp(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = new HttpClient(mock);
        var adapter = new FirecrawlSearchAdapter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExtractAsync(
            Config(SearchProviderTypes.Firecrawl, "https://api.firecrawl.dev"), "key",
            new ExtractRequest(["https://a.example/cancel"]), client, cts.Token));
    }
}
