using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Deadline REST per route (GA-01): setelah headers diterima, body yang menggantung tetap dibatasi
/// <c>Proxy:UpstreamTimeoutSeconds</c> — body sukses, body error, dan event pertama stream sebelum commit — tanpa
/// mengubah pembatalan klien (499), tanpa ikut membatalkan stream yang sudah diserahkan ke pompa, dan tanpa
/// mengubah semantik <c>HttpClient.Timeout</c> (yang berhenti begitu headers tiba pada <c>ResponseHeadersRead</c>).
/// </summary>
public class RestDeadlineTests(TestDb db) : GatewayTestBase(db)
{
    private const string Alias = "gpt-deadline";

    // --- bantuan transport ----------------------------------------------------------------

    private static HttpRequestMessage BuildRequest(string apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        return request;
    }

    /// <summary>Respons non-SSE upstream dengan body yang dikendalikan test (bisa menggantung setelah headers).</summary>
    private static HttpResponseMessage JsonResponse(int status, GatedBody body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
    }

    private static HttpResponseMessage Sse(GatedBody body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // --- bantuan SSE ----------------------------------------------------------------------

    private static string Delta(string id, string content) =>
        $$"""{"id":"{{id}}","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{"role":"assistant","content":"{{content}}"},"finish_reason":null}]}""";

    private static string Finish(string id, string reason = "stop") =>
        $$"""{"id":"{{id}}","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{},"finish_reason":"{{reason}}"}]}""";

    /// <summary>Rangkai event SSE dari payload data; <c>[DONE]</c> ditulis tanpa tanda kutip.</summary>
    private static string Sse(params string[] payloads) =>
        string.Concat(payloads.Select(p => p == "[DONE]" ? "data: [DONE]\n\n" : $"data: {p}\n\n"));

    private sealed record SseEvent(IReadOnlyList<string> Lines)
    {
        public string? Data => Lines.FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal)) is { } line
            ? line[5..].TrimStart(' ')
            : null;

        public bool IsDone => Data == "[DONE]";
        public JsonObject? Json => Data is { } data && data != "[DONE]" ? TryParseObject(data) : null;
    }

    /// <summary>Baca satu event (sampai baris kosong) dengan batas waktu supaya test tidak menggantung.</summary>
    private static async Task<SseEvent?> ReadEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length == 0)
            {
                if (lines.Count == 0) continue;
                return new SseEvent(lines);
            }
            lines.Add(line);
        }
        return lines.Count == 0 ? null : new SseEvent(lines);
    }

    private static async Task<List<SseEvent>> DrainEventsAsync(StreamReader reader)
    {
        var events = new List<SseEvent>();
        while (await ReadEventAsync(reader) is { } next) events.Add(next);
        return events;
    }

    private static JsonObject? TryParseObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? ChunkContent(JsonObject? chunk) =>
        chunk?["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice
            ? (string?)choice["delta"]?["content"]
            : null;

    private static string? ChunkFinish(JsonObject? chunk) =>
        chunk?["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice
            ? (string?)choice["finish_reason"]
            : null;

    // --- bantuan database/route -----------------------------------------------------------

    private async Task<UsageLog> LogAsync(long tenantId)
    {
        await using var ctx = Db.NewContext(tenantId);
        return await ctx.UsageLogs.AsNoTracking().SingleAsync();
    }

    /// <summary>Polling berbatas (bukan tidur tetap) sampai kondisi database terpenuhi.</summary>
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

    /// <summary>Provider kedua sebagai route cadangan (priority 1).</summary>
    private Task AddSecondRouteAsync(Scenario s) =>
        WithTenantAsync(s.TenantId, async (prov, db) =>
        {
            var second = await prov.CreateCustomProviderAsync("up2", "https://up2.test/v1", "sk-second-5678", null, null, null, default);
            db.ModelRoutes.Add(new ModelRoute { ModelId = s.ModelId, ProviderId = second.Id, UpstreamModel = "real-model-2", Priority = 1 });
            await db.SaveChangesAsync();
            return 0;
        });

    // --- skenario -------------------------------------------------------------------------

    [Fact]
    public async Task Headers_then_stalled_success_body_hits_the_deadline_and_frees_the_slot()
    {
        using var factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Proxy:UpstreamTimeoutSeconds", "1");
            b.UseSetting("Proxy:MaxConcurrentPerTenant", "1");
        });
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var stalled = new GatedBody();
        Upstream.Respond = _ => JsonResponse(200, stalled);
        stalled.Write("{\"id\":\"chatcmpl-stall\""); // headers sudah sampai; body dimulai lalu menggantung

        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = BuildRequest(s.ApiKey, Chat(Alias));
        var started = Stopwatch.StartNew();
        var response = await client.SendAsync(request, backstop.Token);
        started.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("upstream_timeout", ErrorCode(await BodyAsync(response)));
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(10));
        await stalled.Disposed.WaitAsync(TimeSpan.FromSeconds(10)); // respons yang gagal dibuang, bukan ditahan

        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 504, "upstream_failed", 1), (log.Status, log.HttpStatus, log.DeniedReason, log.Attempts));
        // Headers sudah diterima sebelum body menggantung: status dan TTFB yang diketahui tetap tercatat.
        Assert.Equal(200, log.UpstreamStatus);
        Assert.NotNull(log.TtfbMs);

        // Slot tenant dilepas: permintaan berikutnya langsung dilayani.
        Upstream.Respond = _ => FakeUpstream.Json(200, FakeUpstream.OkBody);
        using var nextRequest = BuildRequest(s.ApiKey, Chat(Alias));
        var next = await client.SendAsync(nextRequest);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Stalled_error_body_hits_the_deadline_and_falls_back_to_the_next_route()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:UpstreamTimeoutSeconds", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        await AddSecondRouteAsync(s);
        var stalled = new GatedBody();
        Upstream.Respond = c => c.Uri.Host == "up1.test" ? JsonResponse(500, stalled) : FakeUpstream.Json(200, FakeUpstream.OkBody);
        stalled.Write("{\"error\":\"status"); // body error menggantung setelah headers

        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = BuildRequest(s.ApiKey, Chat(Alias));
        var started = Stopwatch.StartNew();
        var response = await client.SendAsync(request, backstop.Token);
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // fallback sebelum commit
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(12));
        Assert.Equal(["up1.test", "up2.test"], Upstream.Requests.Select(r => r.Uri.Host).ToArray());
        await stalled.Disposed.WaitAsync(TimeSpan.FromSeconds(10)); // respons error yang menggantung dibuang

        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Ok, 200, 2, true), (log.Status, log.HttpStatus, log.Attempts, log.FallbackUsed));
    }

    [Fact]
    public async Task Client_cancellation_during_a_stalled_body_is_recorded_as_499()
    {
        // Deadline percobaan sengaja jauh: yang menghentikan pembacaan harus pembatalan klien, bukan deadline.
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:UpstreamTimeoutSeconds", "30"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var stalled = new GatedBody();
        stalled.FailOnCancel(() => new IOException("koneksi ditutup saat klien membatalkan"));
        Upstream.Respond = _ => JsonResponse(200, stalled);
        stalled.Write("{\"id\":");

        using var cts = new CancellationTokenSource();
        using var request = BuildRequest(s.ApiKey, Chat(Alias));
        var send = client.SendAsync(request, cts.Token);
        Assert.True(await EventuallyAsync(() => Task.FromResult(Upstream.Requests.Count == 1)), "upstream belum menerima permintaan");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.True(await EventuallyAsync(async () =>
        {
            await using var ctx = Db.NewContext(s.TenantId);
            return await ctx.UsageLogs.AnyAsync(l => l.HttpStatus == 499);
        }), "baris usage 499 tidak muncul setelah klien membatalkan body yang menggantung");

        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 499, 1), (log.Status, log.HttpStatus, log.Attempts));
    }

    [Fact]
    public async Task Body_finishing_before_the_deadline_succeeds()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:UpstreamTimeoutSeconds", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => JsonResponse(200, body);

        // Body datang dua potong dengan jeda, tetapi selesai jauh sebelum deadline satu detik.
        var payload = FakeUpstream.OkBody;
        body.Write(payload[..(payload.Length / 2)]);
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            body.Write(payload[(payload.Length / 2)..]);
            body.Complete();
        });

        using var request = BuildRequest(s.ApiKey, Chat(Alias));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await BodyAsync(response);
        Assert.Equal(Alias, (string?)json["model"]);
        var choice = Assert.Single(json["choices"]!.AsArray());
        Assert.Equal("hi", (string?)choice!["message"]!["content"]);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Ok, 200, 1), (log.Status, log.HttpStatus, log.Attempts));
    }

    [Fact]
    public async Task Stream_without_a_first_event_falls_back_after_the_deadline()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:UpstreamTimeoutSeconds", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        await AddSecondRouteAsync(s);
        var stalled = new GatedBody(); // SSE 2xx tanpa satu baris pun
        var fallback = new GatedBody();
        fallback.Write(Sse(Delta("c9", "fallback"), Finish("c10"), "[DONE]"));
        fallback.Complete();
        Upstream.Respond = c => c.Uri.Host == "up1.test" ? Sse(stalled) : Sse(fallback);

        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = BuildRequest(s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var started = Stopwatch.StartNew();
        var response = await client.SendAsync(request, backstop.Token);
        started.Stop();

        // Event pertama tidak pernah datang sebelum deadline dan stream belum commit: route berikutnya dipakai.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(12));
        Assert.Equal(["up1.test", "up2.test"], Upstream.Requests.Select(r => r.Uri.Host).ToArray());
        await stalled.Disposed.WaitAsync(TimeSpan.FromSeconds(10));
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var events = new List<SseEvent>();
        while (await ReadEventAsync(reader) is { } next) events.Add(next);
        Assert.Equal("fallback", ChunkContent(events[0].Json));
        Assert.True(events[^1].IsDone);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Ok, 200, 2, true), (log.Status, log.HttpStatus, log.Attempts, log.FallbackUsed));
    }

    [Fact]
    public async Task Accepted_stream_keeps_running_past_the_route_deadline()
    {
        using var factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Proxy:UpstreamTimeoutSeconds", "1"); // batas diam sekaligus batas operasi REST
            b.UseSetting("Proxy:MaxStreamSeconds", "60");
        });
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => Sse(body);
        body.Write(Sse(Delta("c1", "ha")));

        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = BuildRequest(s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, backstop.Token);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(backstop.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ha", ChunkContent((await ReadEventAsync(reader))!.Json));

        // Stream hidup lebih lama dari deadline percobaan (1 detik) dengan jeda di bawah batas diam: kalau token
        // percobaan ikut membatalkan body setelah serah terima, event berikutnya tidak akan pernah tiba.
        for (var i = 2; i <= 4; i++)
        {
            await Task.Delay(500);
            body.Write(Sse(Delta($"c{i}", "x")));
        }
        body.Write(Sse(Finish("c5"), "[DONE]"));
        body.Complete();

        var rest = await DrainEventsAsync(reader);
        Assert.Equal(5, rest.Count);
        Assert.Equal("stop", ChunkFinish(rest[3].Json));
        Assert.True(rest[4].IsDone);
        Assert.True(await EventuallyAsync(async () =>
        {
            await using var ctx = Db.NewContext(s.TenantId);
            return await ctx.UsageLogs.AnyAsync(l => l.HttpStatus == 200);
        }), "baris usage stream belum tercatat");
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Ok, 200, 1), (log.Status, log.HttpStatus, log.Attempts));
    }

    [Fact]
    public async Task Transport_failure_when_the_deadline_fires_is_still_a_timeout()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:UpstreamTimeoutSeconds", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var stalled = new GatedBody();
        stalled.FailOnCancel(() => new IOException("koneksi ditutup saat deadline")); // transport, bukan OCE
        Upstream.Respond = _ => JsonResponse(200, stalled);
        stalled.Write("{\"id\":");

        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = BuildRequest(s.ApiKey, Chat(Alias));
        var response = await client.SendAsync(request, backstop.Token);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("upstream_timeout", ErrorCode(await BodyAsync(response)));
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 504, 1), (log.Status, log.HttpStatus, log.Attempts));
        Assert.Equal(200, log.UpstreamStatus);
    }

    /// <summary>
    /// Body respons upstream yang dikendalikan test: potongan ditulis kapan saja, ditutup, atau digagalkan.
    /// ReadAsync menghormati token pembatalan sehingga deadline dan pembatalan klien bisa diuji tanpa waktu tetap.
    /// </summary>
    private sealed class GatedBody : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _pending = [];
        private int _offset;
        private Func<Exception>? _failOnCancel;

        /// <summary>Selesai ketika stream ini ditutup gateway (mis. respons dibuang setelah deadline).</summary>
        public Task Disposed => _disposed.Task;

        public void Write(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void Complete() => _chunks.Writer.TryComplete();

        /// <summary>Gagalkan pembacaan dengan exception transport ini saat token membatalkannya (bukan OCE).</summary>
        public void FailOnCancel(Func<Exception> failure) => _failOnCancel = failure;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _pending.Length)
            {
                try
                {
                    if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                }
                catch (OperationCanceledException) when (_failOnCancel is { } fail)
                {
                    throw fail();
                }
                if (_chunks.Reader.TryRead(out var next) && next is not null)
                {
                    _pending = next;
                    _offset = 0;
                }
            }

            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            if (count == 0) return 0;
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _disposed.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
