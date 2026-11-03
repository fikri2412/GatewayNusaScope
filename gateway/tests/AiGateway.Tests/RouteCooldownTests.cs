using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using AiGateway.Core.Proxy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

/// <summary>Jam manual untuk test cooldown: waktu hanya bergerak bila test memajukannya.</summary>
internal sealed class CooldownClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// GB-01, unit: <see cref="RouteCooldownTracker"/> dengan jam palsu. Hanya kegagalan provider berturut-turut yang
/// memicu cooldown; sesudah TTL tepat satu probe; sukses memulihkan; tenant dan route lain tidak terpengaruh;
/// <c>Retry-After</c> dihormati dan dibatasi.
/// </summary>
public class RouteCooldownTrackerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static (RouteCooldownTracker Tracker, CooldownClock Clock) Create(Action<ProxyOptions>? tweak = null)
    {
        var options = new ProxyOptions { CooldownFailureThreshold = 3, CooldownSeconds = 30, CooldownWindowSeconds = 60 };
        tweak?.Invoke(options);
        var clock = new CooldownClock(Epoch);
        return (new RouteCooldownTracker(clock, Options.Create(options)), clock);
    }

    [Fact]
    public void Failures_below_the_threshold_do_not_cool_the_route()
    {
        var (tracker, _) = Create();
        Assert.False(tracker.RecordFailure(1, 10));
        Assert.False(tracker.RecordFailure(1, 10));
        Assert.Null(tracker.CoolingUntil(1, 10));
        Assert.True(tracker.RecordFailure(1, 10));
        Assert.Equal(Epoch.AddSeconds(30), tracker.CoolingUntil(1, 10));
    }

    [Fact]
    public void A_success_resets_the_streak()
    {
        var (tracker, _) = Create();
        tracker.RecordFailure(1, 10);
        tracker.RecordFailure(1, 10);
        tracker.RecordSuccess(1, 10);
        tracker.RecordFailure(1, 10);
        tracker.RecordFailure(1, 10);
        Assert.Null(tracker.CoolingUntil(1, 10));
    }

    [Fact]
    public void Failures_older_than_the_window_are_not_consecutive()
    {
        var (tracker, clock) = Create();
        tracker.RecordFailure(1, 10);
        tracker.RecordFailure(1, 10);
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.False(tracker.RecordFailure(1, 10)); // hitungan mulai dari satu lagi
        Assert.Null(tracker.CoolingUntil(1, 10));
    }

    [Fact]
    public void Other_tenants_and_routes_are_unaffected()
    {
        var (tracker, _) = Create();
        for (var i = 0; i < 3; i++) tracker.RecordFailure(1, 10);

        Assert.NotNull(tracker.CoolingUntil(1, 10));
        Assert.Null(tracker.CoolingUntil(2, 10)); // tenant lain, route dengan id yang sama
        Assert.Null(tracker.CoolingUntil(1, 11)); // route lain milik tenant yang sama
        Assert.True(tracker.TryBegin(2, 10, out var probe));
        Assert.False(probe);
    }

    [Fact]
    public void After_the_ttl_exactly_one_probe_is_allowed_and_success_recovers_the_route()
    {
        var (tracker, clock) = Create();
        for (var i = 0; i < 3; i++) tracker.RecordFailure(1, 10);
        Assert.False(tracker.TryBegin(1, 10, out _)); // masih cooldown

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Null(tracker.CoolingUntil(1, 10)); // boleh dicoba lagi
        Assert.True(tracker.TryBegin(1, 10, out var firstProbe));
        Assert.True(firstProbe);
        Assert.False(tracker.TryBegin(1, 10, out var secondProbe)); // probe pertama masih berjalan
        Assert.False(secondProbe);
        Assert.NotNull(tracker.CoolingUntil(1, 10)); // perencana juga melihatnya sedang dipegang

        tracker.RecordSuccess(1, 10);
        Assert.Null(tracker.CoolingUntil(1, 10));
        Assert.True(tracker.TryBegin(1, 10, out var afterRecovery));
        Assert.False(afterRecovery);
    }

    [Fact]
    public void A_failed_probe_cools_the_route_again_immediately()
    {
        var (tracker, clock) = Create();
        for (var i = 0; i < 3; i++) tracker.RecordFailure(1, 10);
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.True(tracker.TryBegin(1, 10, out var probe));
        Assert.True(probe);

        Assert.True(tracker.RecordFailure(1, 10)); // satu kegagalan sudah cukup, tanpa menunggu ambang lagi
        Assert.Equal(clock.Now.AddSeconds(30), tracker.CoolingUntil(1, 10));
    }

    [Fact]
    public void Releasing_a_probe_without_a_verdict_lets_the_next_request_probe()
    {
        var (tracker, clock) = Create();
        for (var i = 0; i < 3; i++) tracker.RecordFailure(1, 10);
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.True(tracker.TryBegin(1, 10, out _));

        tracker.ReleaseProbe(1, 10); // mis. klien memutus koneksi
        Assert.True(tracker.TryBegin(1, 10, out var probe));
        Assert.True(probe);
    }

    [Fact]
    public void An_abandoned_probe_lease_expires_instead_of_locking_the_route()
    {
        var (tracker, clock) = Create(o => o.UpstreamTimeoutSeconds = 10);
        for (var i = 0; i < 3; i++) tracker.RecordFailure(1, 10);
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.True(tracker.TryBegin(1, 10, out _));
        Assert.False(tracker.TryBegin(1, 10, out _));

        clock.Now += TimeSpan.FromSeconds(16); // lewat UpstreamTimeoutSeconds + 5
        Assert.True(tracker.TryBegin(1, 10, out var probe));
        Assert.True(probe);
    }

    [Fact]
    public void Retry_after_cools_the_route_at_once_and_is_capped()
    {
        var (tracker, _) = Create(o => o.CooldownMaxSeconds = 300);
        Assert.True(tracker.RecordFailure(1, 10, "120")); // kegagalan pertama sudah cukup karena provider yang meminta
        Assert.Equal(Epoch.AddSeconds(120), tracker.CoolingUntil(1, 10));

        Assert.True(tracker.RecordFailure(1, 11, "86400"));
        Assert.Equal(Epoch.AddSeconds(300), tracker.CoolingUntil(1, 11)); // dibatasi
    }

    [Fact]
    public void Retry_after_as_an_http_date_is_honoured_and_garbage_is_ignored()
    {
        var (tracker, _) = Create();
        Assert.True(tracker.RecordFailure(1, 10, Epoch.AddSeconds(45).ToString("r")));
        Assert.Equal(Epoch.AddSeconds(45), tracker.CoolingUntil(1, 10));

        Assert.False(tracker.RecordFailure(1, 11, "segera")); // tak terbaca → hitungan biasa
        Assert.False(tracker.RecordFailure(1, 12, Epoch.AddSeconds(-5).ToString("r"))); // sudah lewat
        Assert.Null(tracker.CoolingUntil(1, 11));
        Assert.Null(tracker.CoolingUntil(1, 12));
    }

    [Fact]
    public void Zero_cooldown_seconds_disables_the_feature()
    {
        var (tracker, _) = Create(o => o.CooldownSeconds = 0);
        for (var i = 0; i < 10; i++) Assert.False(tracker.RecordFailure(1, 10, "60"));
        Assert.Null(tracker.CoolingUntil(1, 10));
        Assert.True(tracker.TryBegin(1, 10, out var probe));
        Assert.False(probe);
    }
}

/// <summary>
/// GB-01, lewat host sungguhan dengan upstream palsu: route yang terus gagal dilewati, satu probe memulihkannya, satu-satunya
/// route tetap dicoba, 4xx klien tidak memicu cooldown, <c>Retry-After</c> 429 dihormati, cooldown per tenant, dan deadline
/// total membatasi seluruh rantai route tanpa memotong stream yang sudah commit.
/// </summary>
public class RouteCooldownProxyTests(TestDb db) : GatewayTestBase(db)
{
    private const string Alias = "gpt-cooldown";
    private const string Down = """{"error":{"message":"down","type":"server_error"}}""";

    private static HttpRequestMessage BuildRequest(string apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        return request;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string apiKey, string extra = "")
    {
        using var request = BuildRequest(apiKey, Chat(Alias, extra));
        return await client.SendAsync(request);
    }

    private string[] Hosts() => Upstream.Requests.Select(r => r.Uri.Host).ToArray();

    private async Task<List<UsageLog>> LogsAsync(long tenantId)
    {
        await using var ctx = Db.NewContext(tenantId);
        return await ctx.UsageLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
    }

    /// <summary>Route cadangan ke provider baru bernama host <paramref name="host"/> (<c>https://{host}.test/v1</c>).</summary>
    private Task AddRouteAsync(Scenario s, string host, int priority = 1) =>
        WithTenantAsync(s.TenantId, async (prov, ctx) =>
        {
            var provider = await prov.CreateCustomProviderAsync(host, $"https://{host}.test/v1", "sk-extra-5678", null, null, null, default);
            ctx.ModelRoutes.Add(new ModelRoute { ModelId = s.ModelId, ProviderId = provider.Id, UpstreamModel = $"real-{host}", Priority = priority });
            await ctx.SaveChangesAsync();
            return 0;
        });

    private WebApplicationFactoryHandle Host(CooldownClock? clock = null, params (string Key, string Value)[] settings)
    {
        var factory = Factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            if (clock is not null) b.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        return new WebApplicationFactoryHandle(factory, factory.CreateClient());
    }

    private sealed record WebApplicationFactoryHandle(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    // --- cooldown -------------------------------------------------------------------------

    [Fact]
    public async Task A_route_that_keeps_failing_is_skipped_so_later_requests_go_straight_to_the_fallback()
    {
        using var host = Host(null, ("Proxy:CooldownFailureThreshold", "2"));
        var s = await SetupAsync(alias: Alias);
        await AddRouteAsync(s, "up2");
        Upstream.Respond = c => c.Uri.Host == "up1.test" ? FakeUpstream.Json(503, Down) : FakeUpstream.Json(200, FakeUpstream.OkBody);

        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await PostAsync(host.Client, s.ApiKey)).StatusCode);
        Assert.Equal(["up1.test", "up2.test", "up1.test", "up2.test"], Hosts());

        var third = await PostAsync(host.Client, s.ApiKey);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(["up1.test", "up2.test", "up1.test", "up2.test", "up2.test"], Hosts()); // up1 dilewati

        // attempts dan fallback_used tetap jujur: permintaan ke-3 memang hanya satu percobaan dan bukan fallback.
        var logs = await LogsAsync(s.TenantId);
        Assert.Equal([(2, true), (2, true), (1, false)], logs.Select(l => (l.Attempts, l.FallbackUsed)).ToArray());
    }

    [Fact]
    public async Task After_the_ttl_one_probe_recovers_a_healthy_route()
    {
        var clock = new CooldownClock(DateTimeOffset.UtcNow);
        using var host = Host(clock, ("Proxy:CooldownFailureThreshold", "2"), ("Proxy:CooldownSeconds", "30"));
        var s = await SetupAsync(alias: Alias);
        await AddRouteAsync(s, "up2");
        var up1Healthy = false;
        Upstream.Respond = c => c.Uri.Host == "up1.test" && !up1Healthy ? FakeUpstream.Json(503, Down) : FakeUpstream.Json(200, FakeUpstream.OkBody);

        for (var i = 0; i < 2; i++) await PostAsync(host.Client, s.ApiKey);
        await PostAsync(host.Client, s.ApiKey);
        Assert.Equal("up2.test", Hosts()[^1]); // cooldown aktif

        up1Healthy = true;
        await PostAsync(host.Client, s.ApiKey);
        Assert.Equal("up2.test", Hosts()[^1]); // TTL belum habis: tetap dilewati

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(host.Client, s.ApiKey)).StatusCode);
        Assert.Equal("up1.test", Hosts()[^1]); // probe berhasil di route utama
        await PostAsync(host.Client, s.ApiKey);
        Assert.Equal("up1.test", Hosts()[^1]); // pulih: dipakai seperti biasa

        var logs = await LogsAsync(s.TenantId);
        Assert.Equal((1, false), (logs[^1].Attempts, logs[^1].FallbackUsed));
    }

    [Fact]
    public async Task A_failed_probe_puts_the_route_back_to_sleep_with_a_single_extra_attempt()
    {
        var clock = new CooldownClock(DateTimeOffset.UtcNow);
        using var host = Host(clock, ("Proxy:CooldownFailureThreshold", "2"), ("Proxy:CooldownSeconds", "30"));
        var s = await SetupAsync(alias: Alias);
        await AddRouteAsync(s, "up2");
        Upstream.Respond = c => c.Uri.Host == "up1.test" ? FakeUpstream.Json(503, Down) : FakeUpstream.Json(200, FakeUpstream.OkBody);

        for (var i = 0; i < 3; i++) await PostAsync(host.Client, s.ApiKey);
        var before = Upstream.Requests.Count;

        clock.Now += TimeSpan.FromSeconds(31);
        await PostAsync(host.Client, s.ApiKey); // probe: up1 masih mati → fallback ke up2
        Assert.Equal(["up1.test", "up2.test"], Hosts()[before..]);
        await PostAsync(host.Client, s.ApiKey); // langsung cooldown lagi, tanpa menunggu ambang
        Assert.Equal("up2.test", Hosts()[^1]);
        Assert.Equal(before + 3, Upstream.Requests.Count);
    }

    [Fact]
    public async Task The_only_route_is_still_tried_while_cooling_and_the_client_gets_the_real_failure()
    {
        using var host = Host(null, ("Proxy:CooldownFailureThreshold", "2"));
        var s = await SetupAsync(alias: Alias);
        Upstream.Respond = _ => FakeUpstream.Json(503, Down);

        for (var i = 0; i < 5; i++)
        {
            var response = await PostAsync(host.Client, s.ApiKey);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); // kegagalan upstream sebenarnya, bukan 503 palsu
            Assert.Equal("upstream_error", ErrorCode(await BodyAsync(response)));
        }
        Assert.Equal(5, Upstream.Requests.Count); // tiap permintaan tetap mencoba satu-satunya route

        Upstream.Respond = _ => FakeUpstream.Json(200, FakeUpstream.OkBody);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(host.Client, s.ApiKey)).StatusCode); // pulih tanpa menunggu TTL
    }

    [Fact]
    public async Task Client_errors_never_trigger_a_cooldown()
    {
        using var host = Host(null, ("Proxy:CooldownFailureThreshold", "2"));
        var s = await SetupAsync(alias: Alias);
        await AddRouteAsync(s, "up2");
        Upstream.Respond = _ => FakeUpstream.Json(400, """{"error":{"message":"bad request","type":"invalid_request_error"}}""");

        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(host.Client, s.ApiKey)).StatusCode);
        Assert.All(Hosts(), h => Assert.Equal("up1.test", h)); // 400 diteruskan apa adanya; route lain tidak pernah dipakai

        Upstream.Respond = _ => FakeUpstream.Json(200, FakeUpstream.OkBody);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(host.Client, s.ApiKey)).StatusCode);
        Assert.Equal("up1.test", Hosts()[^1]); // up1 tidak pernah tidur
        Assert.DoesNotContain("up2.test", Hosts());
    }

    [Fact]
    public async Task A_rate_limited_route_with_retry_after_is_skipped_at_once()
    {
        using var host = Host(null);
        var s = await SetupAsync(alias: Alias);
        await AddRouteAsync(s, "up2");
        Upstream.Respond = c =>
        {
            if (c.Uri.Host != "up1.test") return FakeUpstream.Json(200, FakeUpstream.OkBody);
            var limited = FakeUpstream.Json(429, """{"error":{"message":"slow down"}}""");
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return limited;
        };

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(host.Client, s.ApiKey)).StatusCode);
        Assert.Equal(["up1.test", "up2.test"], Hosts());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(host.Client, s.ApiKey)).StatusCode);
        Assert.Equal(["up1.test", "up2.test", "up2.test"], Hosts()); // satu 429 ber-Retry-After sudah cukup
    }

    [Fact]
    public async Task Cooldown_is_per_tenant()
    {
        using var host = Host(null, ("Proxy:CooldownFailureThreshold", "2"));
        var a = await SetupAsync(alias: Alias);
        var b = await SetupAsync(alias: Alias);
        await AddRouteAsync(a, "up2");
        await AddRouteAsync(b, "up2");
        Upstream.Respond = c => c.Uri.Host == "up1.test" ? FakeUpstream.Json(503, Down) : FakeUpstream.Json(200, FakeUpstream.OkBody);

        for (var i = 0; i < 3; i++) await PostAsync(host.Client, a.ApiKey);
        Assert.Equal("up2.test", Hosts()[^1]);
        var before = Upstream.Requests.Count;

        await PostAsync(host.Client, b.ApiKey);
        Assert.Equal(["up1.test", "up2.test"], Hosts()[before..]); // tenant B tidak ikut menanggung riwayat tenant A
    }

    // --- deadline total -------------------------------------------------------------------

    [Fact]
    public async Task The_request_deadline_bounds_the_whole_route_chain()
    {
        using var host = Host(null, ("Proxy:UpstreamTimeoutSeconds", "3"), ("Proxy:RequestDeadlineSeconds", "4"));
        var s = await SetupAsync(alias: Alias);
        await AddRouteAsync(s, "up2", 1);
        await AddRouteAsync(s, "up3", 2);
        Upstream.Respond = _ => StalledJson();

        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = BuildRequest(s.ApiKey, Chat(Alias));
        var started = Stopwatch.StartNew();
        var response = await host.Client.SendAsync(request, backstop.Token);
        started.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("upstream_timeout", ErrorCode(await BodyAsync(response)));
        // Tanpa deadline total tiga route yang menggantung memakan ±9 detik; dengan deadline 4 detik rantai berhenti.
        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(7));
        Assert.Equal(["up1.test", "up2.test"], Hosts()); // route ketiga tidak pernah dicoba

        var log = Assert.Single(await LogsAsync(s.TenantId));
        Assert.Equal((UsageStatuses.Error, 504, "request_deadline_exceeded", 2), (log.Status, log.HttpStatus, log.DeniedReason, log.Attempts));
    }

    [Fact]
    public async Task The_request_deadline_does_not_cut_a_stream_that_already_committed()
    {
        using var host = Host(null, ("Proxy:UpstreamTimeoutSeconds", "30"), ("Proxy:RequestDeadlineSeconds", "1"), ("Proxy:MaxStreamSeconds", "60"));
        var s = await SetupAsync(alias: Alias);
        const string first = """data: {"id":"c1","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{"role":"assistant","content":"awal"},"finish_reason":null}]}""" + "\n\n";
        const string rest = """data: {"id":"c2","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{"content":"akhir"},"finish_reason":"stop"}]}""" + "\n\ndata: [DONE]\n\n";
        Upstream.Respond = _ =>
        {
            var content = new StreamContent(new TimedSseBody(first, rest, TimeSpan.FromSeconds(2))); // lanjutan datang setelah deadline total
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = BuildRequest(s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var started = Stopwatch.StartNew();
        var response = await host.Client.SendAsync(request, backstop.Token);
        var text = await response.Content.ReadAsStringAsync(backstop.Token);
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("awal", text);
        Assert.Contains("akhir", text); // event setelah deadline total tetap sampai
        Assert.Contains("[DONE]", text);
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(1.5), "stream selesai terlalu cepat; jeda 2 detik tidak terjadi");

        Assert.True(await EventuallyAsync(async () => (await LogsAsync(s.TenantId)).Any(l => l.HttpStatus == 200)), "baris usage stream belum tercatat");
        var log = Assert.Single(await LogsAsync(s.TenantId));
        Assert.Equal((UsageStatuses.Ok, 200, 1), (log.Status, log.HttpStatus, log.Attempts));
    }

    // --- bantuan --------------------------------------------------------------------------

    /// <summary>Headers 200 segera, body tidak pernah selesai sampai dibatalkan.</summary>
    private static HttpResponseMessage StalledJson()
    {
        var content = new StreamContent(new StalledBody());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledBody : ReadOnlyStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>Potongan pertama langsung, potongan kedua setelah jeda, lalu selesai.</summary>
    private sealed class TimedSseBody(string first, string rest, TimeSpan gap) : ReadOnlyStream
    {
        private int _stage;
        private byte[] _current = [];
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (_stage == 0) _current = Encoding.UTF8.GetBytes(first);
                else if (_stage == 1)
                {
                    await Task.Delay(gap, cancellationToken);
                    _current = Encoding.UTF8.GetBytes(rest);
                }
                else return 0;
                _stage++;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }
}
