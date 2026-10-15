using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Worker.Operator.Tools;
using Xunit;

namespace OperatorAi.Tests;

/// <summary>
/// Precedence fallback GatewayWebSearch (hanya 503/tidak terjangkau), sanitasi error/log, guard alamat publik
/// FallbackWebSearch, dan batas "jawaban instan" DuckDuckGo yang jujur. Tanpa database.
/// </summary>
public class ChatbotWebSearchTests
{
    private sealed class MockHttp(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class CollectLogs : ILogger<GatewayWebSearch>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    /// <summary>Fallback yang menghitung panggilan; respons HTTP-nya bisa dikarang per test.</summary>
    private sealed class FallbackSpy
    {
        private int _calls;

        public FallbackSpy(Func<HttpResponseMessage>? response = null)
        {
            Value = new FallbackWebSearch(new HttpClient(new MockHttp(_ =>
            {
                Interlocked.Increment(ref _calls);
                return response?.Invoke() ?? Json("{}");
            })));
        }

        public int Calls => Volatile.Read(ref _calls);
        public FallbackWebSearch Value { get; }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GatewayWebSearch Gateway(HttpMessageHandler handler, FallbackWebSearch fallback, ILogger<GatewayWebSearch>? logger = null) =>
        new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            BaseUrl = "http://localhost:5090/v1/",
            ApiKey = "test-gw-key"
        }), fallback, logger);

    [Fact]
    public async Task Gateway_calls_search_endpoint_with_gateway_auth_and_parses_compact_results()
    {
        var mock = new MockHttp(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.EndsWith("/v1/search", req.RequestUri!.ToString());
            Assert.Equal("Bearer test-gw-key", req.Headers.Authorization!.ToString());
            return Json("""
                {
                  "provider": "tavily",
                  "results": [
                    { "title": "Hasil 1", "url": "https://example.com/1", "snippet": "Ringkasan snippet 1" }
                  ]
                }
                """);
        });

        var result = await Gateway(mock, new FallbackSpy().Value).SearchAsync("kueri uji", 3, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("tavily", result.Provider);
        Assert.Equal(("Hasil 1", "https://example.com/1", "Ringkasan snippet 1"),
            (result.Results[0].Title, result.Results[0].Url, result.Results[0].Snippet));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Gateway_falls_back_only_on_503_or_unreachable(HttpStatusCode status)
    {
        var spy = new FallbackSpy();
        var result = await Gateway(new MockHttp(_ => Json("""{"error":{"message":"RAHASIA-BODY-PENYEDIA"}}""", status)), spy.Value)
            .SearchAsync("kueri", 3, CancellationToken.None);

        Assert.Equal(0, spy.Calls);
        Assert.Empty(result.Results);
        Assert.Contains($"HTTP {(int)status}", result.Error);
        Assert.DoesNotContain("RAHASIA-BODY-PENYEDIA", result.Error);
    }

    [Fact]
    public async Task Gateway_does_not_fall_back_on_malformed_json()
    {
        var spy = new FallbackSpy();
        var result = await Gateway(new MockHttp(_ => Json("{ bukan json")), spy.Value).SearchAsync("kueri", 3, CancellationToken.None);

        Assert.Equal(0, spy.Calls);
        Assert.Empty(result.Results);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Gateway_falls_back_on_503_only()
    {
        var spy = new FallbackSpy(() => Json("""
            { "Heading": "Fallback Heading", "AbstractText": "Fallback Abstract Text", "AbstractURL": "https://duckduckgo.com/fallback" }
            """));
        var result = await Gateway(new MockHttp(_ => Json("""{"error":{"code":"no_search_providers_available"}}""", HttpStatusCode.ServiceUnavailable)), spy.Value)
            .SearchAsync("fallback query", 3, CancellationToken.None);

        Assert.Equal(1, spy.Calls);
        Assert.Null(result.Error);
        Assert.Equal("duckduckgo_fallback", result.Provider);
        Assert.Contains(result.Results, r => r.Title == "Fallback Heading");
    }

    [Fact]
    public async Task Gateway_falls_back_when_unreachable_or_timed_out()
    {
        var spy = new FallbackSpy(() => Json("{}"));
        var unreachable = await Gateway(new MockHttp(_ => throw new HttpRequestException("gateway mati")), spy.Value)
            .SearchAsync("kueri", 3, CancellationToken.None);
        var timeout = await Gateway(new MockHttp(_ => throw new TaskCanceledException("timeout")), spy.Value)
            .SearchAsync("kueri", 3, CancellationToken.None);

        Assert.Equal(2, spy.Calls);
        Assert.Null(unreachable.Error);
        Assert.Empty(unreachable.Results);
        Assert.Empty(timeout.Results);
    }

    [Fact]
    public async Task Gateway_propagates_cancellation_without_fallback()
    {
        var spy = new FallbackSpy();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Gateway(new MockHttp(_ => throw new OperationCanceledException(cts.Token)), spy.Value).SearchAsync("kueri", 3, cts.Token));

        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task Gateway_extract_parses_pages_and_never_falls_back_on_error_status()
    {
        var spy = new FallbackSpy();
        var ok = await Gateway(new MockHttp(req => Json("""
            {"provider":"firecrawl","pages":[{"url":"https://example.com/a","markdown":"# Judul\nIsi halaman"}]}
            """)), spy.Value).ExtractAsync("https://example.com/a", CancellationToken.None);
        var failed = await Gateway(new MockHttp(_ => Json("""{"error":{"code":"invalid_url"}}""", HttpStatusCode.BadRequest)), spy.Value)
            .ExtractAsync("https://example.com/a", CancellationToken.None);

        Assert.Null(ok.Error);
        Assert.Equal("firecrawl", ok.Provider);
        Assert.Equal("https://example.com/a", ok.Url);
        Assert.Contains("Isi halaman", ok.Markdown);
        Assert.Equal(0, spy.Calls);
        Assert.NotNull(failed.Error);
        Assert.Equal("", failed.Markdown);
    }

    [Fact]
    public async Task Gateway_extract_falls_back_on_503_to_direct_fetch()
    {
        var spy = new FallbackSpy(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><h1>Halo</h1> Dunia</body></html>", Encoding.UTF8, "text/html")
        });

        var result = await Gateway(new MockHttp(_ => Json("{}", HttpStatusCode.ServiceUnavailable)), spy.Value)
            .ExtractAsync("https://example.com/a", CancellationToken.None);

        Assert.Equal(1, spy.Calls);
        Assert.Null(result.Error);
        Assert.Contains("Halo", result.Markdown);
    }

    [Fact]
    public async Task Gateway_logs_and_errors_never_contain_provider_body_or_exception_message()
    {
        var errorLogs = new CollectLogs();
        var spy = new FallbackSpy();
        await Gateway(new MockHttp(_ => Json("""{"error":"RAHASIA-BODY-PENYEDIA"}""", HttpStatusCode.InternalServerError)), spy.Value, errorLogs)
            .SearchAsync("kueri", 3, CancellationToken.None);
        Assert.DoesNotContain(errorLogs.Messages, m => m.Contains("RAHASIA-BODY-PENYEDIA"));

        var networkLogs = new CollectLogs();
        await Gateway(new MockHttp(_ => throw new HttpRequestException("RAHASIA-EXCEPTION")), spy.Value, networkLogs)
            .SearchAsync("kueri", 3, CancellationToken.None);
        Assert.DoesNotContain(networkLogs.Messages, m => m.Contains("RAHASIA-EXCEPTION"));
    }

    [Fact]
    public async Task Fallback_search_returns_true_empty_list_when_no_instant_answer()
    {
        var fallback = new FallbackWebSearch(new HttpClient(new MockHttp(_ => Json("{}"))));

        var result = await fallback.SearchAsync("kueri", 3, CancellationToken.None);

        Assert.Equal("duckduckgo_fallback", result.Provider);
        Assert.Empty(result.Results);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Fallback_failures_are_sanitized_and_cancellation_propagates()
    {
        var fallback = new FallbackWebSearch(new HttpClient(new MockHttp(_ => Json("""{"detail":"RAHASIA-BODY"}""", HttpStatusCode.InternalServerError))));
        var failed = await fallback.ExtractAsync("https://example.com/a", CancellationToken.None);
        Assert.NotNull(failed.Error);
        Assert.Equal("", failed.Markdown);
        Assert.DoesNotContain("RAHASIA-BODY", failed.Error);

        var invalid = await fallback.ExtractAsync("ftp://example.com/a", CancellationToken.None);
        Assert.NotNull(invalid.Error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new FallbackWebSearch(new HttpClient(new MockHttp(_ => throw new OperationCanceledException(cts.Token))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ExtractAsync("https://example.com/a", cts.Token));
    }

    [Fact]
    public async Task Fallback_extract_through_guarded_client_refuses_private_address()
    {
        var fallback = new FallbackWebSearch(new HttpClient(PublicUrlGuard.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) });

        var result = await fallback.ExtractAsync("http://10.0.0.1/", CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("", result.Markdown);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2002::1", false)]
    [InlineData("64:ff9b::808:808", false)]
    public void PublicUrlGuard_allows_only_public_addresses(string ip, bool expected) =>
        Assert.Equal(expected, PublicUrlGuard.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task PublicUrlGuard_handler_refuses_private_and_loopback_targets_at_connect_time()
    {
        using var http = new HttpClient(PublicUrlGuard.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        string[] blocked =
        [
            "http://127.0.0.1/", "http://127.0.0.1:8080/admin", "http://10.0.0.1/", "http://192.168.1.1/",
            "http://169.254.169.254/latest/meta-data/", "http://[::1]/", "http://[::ffff:127.0.0.1]/", "http://[fd00::1]/",
            "http://localhost/"
        ];

        foreach (var url in blocked)
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(url));
    }

    [Fact]
    public void Tool_definitions_expose_fetch_url_and_drop_baca_web()
    {
        var names = ToolDefinitions.All().Select(t => (string)t!["function"]!["name"]!).ToList();

        Assert.Contains(ToolDefinitions.FetchUrl, names);
        Assert.Contains(ToolDefinitions.CariWeb, names);
        Assert.DoesNotContain("baca_web", names);
    }
}

/// <summary>Perilaku tool cari_web/fetch_url lewat <see cref="IWebSearch"/>: framing data tidak tepercaya, batas panjang, validasi URL.</summary>
public class ChatbotToolDispatcherTests : QueueTestBase
{
    private sealed class MockWebSearch(WebSearchResult? search = null, WebExtractResult? extract = null) : IWebSearch
    {
        public int Searches { get; private set; }
        public int Extracts { get; private set; }

        public Task<WebSearchResult> SearchAsync(string query, int count, CancellationToken ct)
        {
            Searches++;
            return Task.FromResult(search ?? new WebSearchResult("mock_provider",
                [new WebSearchResultItem("NusaScope Title", "https://nusascope.local", "NusaScope Snippet")]));
        }

        public Task<WebExtractResult> ExtractAsync(string url, CancellationToken ct)
        {
            Extracts++;
            return Task.FromResult(extract ?? new WebExtractResult("mock_provider", url, "Contoh isi markdown halaman"));
        }
    }

    private static ToolContext Context(AppDbContext db, IWebSearch? search = null) =>
        new(db, Guid.NewGuid(), Policy(), 1, TestModel, WebSearch: search);

    [Fact]
    public async Task Cari_web_frames_results_as_untrusted_data()
    {
        await using var db = NewContext();
        var search = new MockWebSearch();

        var result = await ToolDispatcher.ExecuteAsync(Context(db, search), ToolDefinitions.CariWeb, "{\"kueri\":\"nusascope\"}", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("NusaScope Title", result.Text);
        Assert.Contains("NusaScope Snippet", result.Text);
        Assert.Contains("tidak tepercaya", result.Text);
        Assert.Equal(1, search.Searches);
    }

    [Fact]
    public async Task Cari_web_clips_long_title_and_snippet()
    {
        await using var db = NewContext();
        var search = new MockWebSearch(new WebSearchResult("mock_provider",
            [new WebSearchResultItem(new string('T', 500), "https://x.test/", new string('S', 5000))]));

        var result = await ToolDispatcher.ExecuteAsync(Context(db, search), ToolDefinitions.CariWeb, "{\"kueri\":\"x\"}", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.DoesNotContain(new string('T', 500), result.Text);
        Assert.DoesNotContain(new string('S', 5000), result.Text);
        Assert.True(result.Text.Length < 3000);
    }

    [Fact]
    public async Task Cari_web_distinguishes_empty_results_and_provider_errors()
    {
        await using var db = NewContext();
        var notFound = await ToolDispatcher.ExecuteAsync(
            Context(db, new MockWebSearch(new WebSearchResult("gateway", []))), ToolDefinitions.CariWeb, "{\"kueri\":\"langka\"}", CancellationToken.None);
        var failed = await ToolDispatcher.ExecuteAsync(
            Context(db, new MockWebSearch(new WebSearchResult("gateway", [], "Pencarian web gagal (HTTP 429)."))), ToolDefinitions.CariWeb, "{\"kueri\":\"x\"}", CancellationToken.None);

        Assert.False(notFound.IsError);
        Assert.Contains("Tidak ditemukan hasil pencarian web", notFound.Text);
        Assert.True(failed.IsError);
        Assert.Contains("HTTP 429", failed.Text);
    }

    [Fact]
    public async Task Cari_web_validates_query_and_reports_unconfigured_provider()
    {
        await using var db = NewContext();
        var ctx = Context(db, new MockWebSearch());
        var empty = await ToolDispatcher.ExecuteAsync(ctx, ToolDefinitions.CariWeb, "{\"kueri\":\"\"}", CancellationToken.None);
        var tooLong = await ToolDispatcher.ExecuteAsync(ctx, ToolDefinitions.CariWeb, $"{{\"kueri\":\"{new string('a', 201)}\"}}", CancellationToken.None);
        var unconfigured = await ToolDispatcher.ExecuteAsync(Context(db), ToolDefinitions.CariWeb, "{\"kueri\":\"x\"}", CancellationToken.None);

        Assert.True(empty.IsError);
        Assert.Contains("kueri wajib diisi", empty.Text);
        Assert.True(tooLong.IsError);
        Assert.Contains("kueri maksimal 200 karakter", tooLong.Text);
        Assert.True(unconfigured.IsError);
        Assert.Contains("belum dikonfigurasi", unconfigured.Text);
    }

    [Fact]
    public async Task Fetch_url_uses_IWebSearch_and_frames_content_as_untrusted()
    {
        await using var db = NewContext();
        var search = new MockWebSearch();

        var result = await ToolDispatcher.ExecuteAsync(Context(db, search), ToolDefinitions.FetchUrl, "{\"url\":\"https://example.com/docs\"}", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Contoh isi markdown halaman", result.Text);
        Assert.Contains("tidak tepercaya", result.Text);
        Assert.Equal(1, search.Extracts);
    }

    [Fact]
    public async Task Fetch_url_rejects_invalid_and_private_urls_without_fetching()
    {
        string[] rejected =
        [
            "bukan-url", "ftp://example.com/x", "http://127.0.0.1/", "http://127.0.0.1:8080/admin",
            "http://169.254.169.254/latest/meta-data/", "http://[::1]/", "http://[::ffff:127.0.0.1]/", "http://[fd00::1]/",
            "https://example.com/" + new string('a', 1200)
        ];

        await using var db = NewContext();
        var search = new MockWebSearch();
        foreach (var url in rejected)
        {
            var result = await ToolDispatcher.ExecuteAsync(Context(db, search), ToolDefinitions.FetchUrl, $"{{\"url\":\"{url}\"}}", CancellationToken.None);
            Assert.True(result.IsError);
        }
        Assert.Equal(0, search.Extracts);
    }

    [Fact]
    public async Task Fetch_url_surfaces_extract_error()
    {
        await using var db = NewContext();
        var search = new MockWebSearch(extract: new WebExtractResult("gateway", "https://example.com/a", "", "Pengambilan isi URL gagal (HTTP 400)."));

        var result = await ToolDispatcher.ExecuteAsync(Context(db, search), ToolDefinitions.FetchUrl, "{\"url\":\"https://example.com/a\"}", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("HTTP 400", result.Text);
    }
}
