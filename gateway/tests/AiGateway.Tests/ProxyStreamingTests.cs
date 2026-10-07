using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiGateway.Core.Common;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Streaming SSE di data plane: pengiriman bertahap, kontrak body ke upstream, penanganan chunk usage,
/// validasi <c>stream</c>, fallback sebelum commit, kegagalan di tengah stream (tanpa fallback), timeout diam
/// dan durasi, batas ukuran, klien putus, serta slot konkurensi tenant. Upstream palsu dikendalikan test lewat
/// <see cref="GatedBody"/> supaya urutan kejadian ditentukan test, bukan waktu.
/// </summary>
public class ProxyStreamingTests(TestDb db) : GatewayTestBase(db)
{
    private const string Alias = "gpt-stream";

    /// <summary>Chunk usage-saja seperti yang dikirim OpenCode: <c>choices</c> kosong plus usage.</summary>
    private const string UsageChunk = """{"id":"u1","object":"chat.completion.chunk","model":"real-model","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":5}}}""";

    /// <summary>Chunk dengan usage pada pilihan non-kosong: usage harus dibuang bila klien tidak memintanya.</summary>
    private const string FinishChunkWithUsage = """{"id":"c3","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":20}}""";

    private static string Delta(string id, string content) =>
        $$"""{"id":"{{id}}","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{"role":"assistant","content":"{{content}}"},"finish_reason":null}]}""";

    private static string Finish(string id, string reason = "stop") =>
        $$"""{"id":"{{id}}","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{},"finish_reason":"{{reason}}"}]}""";

    /// <summary>Rangkai event SSE dari payload data; <c>[DONE]</c> ditulis tanpa tanda kutip.</summary>
    private static string Sse(params string[] payloads) =>
        string.Concat(payloads.Select(p => p == "[DONE]" ? "data: [DONE]\n\n" : $"data: {p}\n\n"));

    // --- transport --------------------------------------------------------------------------

    private static HttpRequestMessage BuildRequest(string apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        return request;
    }

    /// <summary>POST dengan header saja: body SSE dibaca bertahap oleh pemanggil.</summary>
    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> StartStreamAsync(
        HttpClient client, string apiKey, string json, CancellationToken ct = default)
    {
        using var request = BuildRequest(apiKey, json);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return (response, new StreamReader(await response.Content.ReadAsStreamAsync(ct)));
    }

    /// <summary>POST biasa; dipakai untuk respons yang bukan SSE.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string apiKey, string json)
    {
        using var request = BuildRequest(apiKey, json);
        return await client.SendAsync(request);
    }

    private static HttpResponseMessage Sse(GatedBody body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Stream upstream yang isinya sudah lengkap lalu ditutup.</summary>
    private static HttpResponseMessage CompletedSse(string text)
    {
        var body = new GatedBody();
        body.Write(text);
        body.Complete();
        return Sse(body);
    }

    /// <summary>Stream upstream 2xx bertipe SSE yang langsung tertutup tanpa satu baris pun.</summary>
    private static HttpResponseMessage EmptySse()
    {
        var body = new GatedBody();
        body.Complete();
        return Sse(body);
    }

    // --- pembacaan SSE ----------------------------------------------------------------------

    private sealed record SseEvent(IReadOnlyList<string> Lines)
    {
        public string Raw => string.Join("\n", Lines);

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

    private static async Task<List<SseEvent>> DrainAsync(StreamReader reader)
    {
        var events = new List<SseEvent>();
        while (await ReadEventAsync(reader) is { } next) events.Add(next);
        return events;
    }

    private static string[] RawEvents(string raw) => raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

    private static JsonObject? EventJson(string block) =>
        block.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal)) is { } line
            ? TryParseObject(line[5..].TrimStart(' '))
            : null;

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

    // --- bantuan database/route -------------------------------------------------------------

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

    /// <summary>Provider kedua sebagai route prioritas 1: dipakai untuk menguji fallback.</summary>
    private Task AddSecondRouteAsync(Scenario s) =>
        WithTenantAsync(s.TenantId, async (prov, db) =>
        {
            var second = await prov.CreateCustomProviderAsync("up2", "https://up2.test/v1", "sk-second-5678", null, null, null, default);
            db.ModelRoutes.Add(new ModelRoute { ModelId = s.ModelId, ProviderId = second.Id, UpstreamModel = "real-model-2", Priority = 1 });
            await db.SaveChangesAsync();
            return 0;
        });

    // --- pengiriman bertahap ----------------------------------------------------------------

    [Fact]
    public async Task Stream_delivers_the_first_event_before_the_upstream_finishes()
    {
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => Sse(body);
        body.Write(Sse(Delta("c1", "ha"))); // event pertama sudah tersedia sebelum permintaan dikirim

        var (response, reader) = await StartStreamAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        // Upstream sengaja belum ditutup: kalau gateway menahan respons sampai upstream selesai, pembacaan ini menggantung.
        var first = await ReadEventAsync(reader);
        Assert.Equal("ha", ChunkContent(first!.Json));
        Assert.False(first.IsDone);

        body.Write(Sse(Delta("c2", "lo"), Finish("c3"), "[DONE]"));
        body.Complete();
        var rest = await DrainAsync(reader);

        Assert.Equal(3, rest.Count);
        Assert.Equal("lo", ChunkContent(rest[0].Json));
        Assert.Equal("stop", ChunkFinish(rest[1].Json));
        Assert.True(rest[2].IsDone);
    }

    [Fact]
    public async Task Stream_rewrites_the_alias_streams_in_order_and_bills_the_usage_chunk()
    {
        var s = await SetupAsync(alias: Alias);
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c1", "ha"), Delta("c2", "lo"), Finish("c3"), UsageChunk, "[DONE]"));

        var (response, reader) = await StartStreamAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var events = await DrainAsync(reader);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("text/event-stream", "utf-8"), (response.Content.Headers.ContentType!.MediaType, response.Content.Headers.ContentType.CharSet));
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        var requestId = Guid.Parse(response.Headers.GetValues("X-Request-Id").Single());

        Assert.Equal(4, events.Count); // klien tidak meminta usage: chunk usage-saja dibuang
        Assert.Equal(("ha", "lo"), (ChunkContent(events[0].Json), ChunkContent(events[1].Json)));
        Assert.Equal("stop", ChunkFinish(events[2].Json));
        Assert.True(events[3].IsDone);
        Assert.All(events.Take(3), e => Assert.Equal(Alias, (string?)e.Json!["model"]));
        Assert.All(events.Take(3), e => Assert.DoesNotContain("\"usage\"", e.Raw));

        var log = await LogAsync(s.TenantId);
        Assert.Equal((requestId, UsageStatuses.Ok, 200, 1, false),
            (log.RequestId, log.Status, log.HttpStatus, log.Attempts, log.FallbackUsed));
        Assert.Equal((10, 20, 4, 5), (log.InputTokens, log.OutputTokens, log.CachedTokens, log.ReasoningTokens));
        Assert.Equal(0.00005m, log.Cost); // (10 x 1 + 20 x 2) / 1M
        Assert.Equal("stop", log.FinishReason);
        Assert.NotNull(log.TtfbMs);
        Assert.Equal((s.ModelId, s.ProviderId, 200), (log.ModelId, log.ProviderId, log.UpstreamStatus));

        await using var ctx = Db.NewContext(s.TenantId);
        var daily = await ctx.UsageDailies.SingleAsync();
        Assert.Equal((1L, 0L, 10L, 20L, 0.00005m), (daily.Requests, daily.Denied, daily.InputTokens, daily.OutputTokens, daily.Cost));
    }

    [Fact]
    public async Task Stream_request_forces_include_usage_and_keeps_other_stream_options()
    {
        var s = await SetupAsync(alias: Alias, maxOutput: 1000);
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c1", "x"), Finish("c2"), "[DONE]"));

        var (_, reader) = await StartStreamAsync(Client, s.ApiKey,
            Chat(Alias, ",\"stream\":true,\"max_tokens\":5000,\"stream_options\":{\"include_usage\":false,\"trace\":\"on\"}"));
        await DrainAsync(reader);

        var sent = JsonNode.Parse(Assert.Single(Upstream.Requests).Body)!;
        Assert.Equal(true, (bool?)sent["stream"]);
        Assert.Equal(1000, (int?)sent["max_tokens"]); // dipotong ke batas model seperti jalur non-streaming
        Assert.Equal(true, (bool?)sent["stream_options"]!["include_usage"]); // dipaksa walau klien mengirim false
        Assert.Equal("on", (string?)sent["stream_options"]!["trace"]);       // opsi lain di dalam stream_options tetap ada

        var second = await SetupAsync(alias: Alias);
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c1", "x"), "[DONE]"));
        var (_, reader2) = await StartStreamAsync(Client, second.ApiKey, Chat(Alias, ",\"stream\":true,\"stream_options\":\"yes\""));
        await DrainAsync(reader2);

        var sent2 = JsonNode.Parse(Upstream.Requests.Last().Body)!;
        Assert.Single(sent2["stream_options"]!.AsObject()); // stream_options non-objek diganti, bukan diteruskan
        Assert.Equal(true, (bool?)sent2["stream_options"]!["include_usage"]);
    }

    // --- chunk usage ------------------------------------------------------------------------

    [Fact]
    public async Task Usage_only_chunk_is_hidden_when_the_client_did_not_ask_for_it()
    {
        var s = await SetupAsync(alias: Alias);
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c1", "ha"), FinishChunkWithUsage, UsageChunk, "[DONE]"));

        var response = await PostAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("\"usage\"", raw); // usage dibuang dari chunk berisi choices...
        Assert.DoesNotContain("\n\n\n", raw);    // ...dan chunk usage-saja dibuang bersama baris kosong pemisahnya
        Assert.EndsWith("data: [DONE]\n\n", raw);
        var blocks = RawEvents(raw);
        Assert.Equal(3, blocks.Length); // delta, finish (tanpa usage), [DONE]
        Assert.Null(EventJson(blocks[1])!["usage"]);
        Assert.Equal("stop", ChunkFinish(EventJson(blocks[1])));
        Assert.Equal(0.00005m, (await LogAsync(s.TenantId)).Cost); // penagihan tetap memakai usage internal
    }

    [Fact]
    public async Task Usage_only_chunk_is_forwarded_when_the_client_asked_for_it()
    {
        var s = await SetupAsync(alias: Alias);
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c1", "ha"), UsageChunk, "[DONE]"));

        var (_, reader) = await StartStreamAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true,\"stream_options\":{\"include_usage\":true}"));
        var events = await DrainAsync(reader);

        Assert.Equal(3, events.Count);
        Assert.Equal("ha", ChunkContent(events[0].Json));
        Assert.Empty(events[1].Json!["choices"]!.AsArray());
        Assert.NotNull(events[1].Json!["usage"]); // klien memintanya: chunk usage diteruskan
        Assert.Equal(Alias, (string?)events[1].Json!["model"]);
        Assert.True(events[2].IsDone);
        Assert.Equal(0.00005m, (await LogAsync(s.TenantId)).Cost);
    }

    // --- validasi stream --------------------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("\"true\"")]
    [InlineData("\"yes\"")]
    [InlineData("{}")]
    public async Task Non_boolean_stream_shapes_are_rejected_with_invalid_stream(string raw)
    {
        var s = await SetupAsync(alias: Alias);

        var response = await PostAsync(Client, s.ApiKey, Chat(Alias, $",\"stream\":{raw}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("invalid_stream", ErrorCode(await BodyAsync(response)));
        Assert.Empty(Upstream.Requests);
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal(("invalid_stream", 400), (log.DeniedReason, log.HttpStatus));
    }

    [Fact]
    public async Task Absent_null_and_false_stream_stay_classic_json()
    {
        var s = await SetupAsync(alias: Alias);

        foreach (var extra in new[] { "", ",\"stream\":false", ",\"stream\":null" })
        {
            var response = await PostAsync(Client, s.ApiKey, Chat(Alias, extra));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            var body = await BodyAsync(response);
            Assert.Equal(Alias, (string?)body["model"]);
            Assert.Equal("hi", (string?)body["choices"]!.AsArray()[0]!["message"]!["content"]);
        }

        Assert.Equal(3, Upstream.Requests.Count);
    }

    [Fact]
    public async Task Policy_denial_on_a_stream_request_is_plain_json()
    {
        var s = await SetupAsync(alias: Alias);
        await WithTenantAsync(s.TenantId, async (prov, _) =>
        {
            await prov.SetPolicyAsync(PolicyScopes.Key, s.KeyId, new PolicySpec(AllowedModels: ["something-else"]), default);
            return 0;
        });

        var response = await PostAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("model_not_allowed", ErrorCode(await BodyAsync(response)));
        Assert.Empty(Upstream.Requests);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Denied, "model_not_allowed:key"), (log.Status, log.DeniedReason));
    }

    // --- fallback sebelum commit ------------------------------------------------------------

    [Theory]
    [InlineData("http_500")]
    [InlineData("empty_stream")]
    [InlineData("first_event_error")]
    public async Task Fallback_before_commit_switches_to_the_next_route(string failure)
    {
        var s = await SetupAsync(alias: Alias);
        await AddSecondRouteAsync(s);
        var fallback = Sse(Delta("c9", "fallback"), Finish("c10"), "[DONE]");
        Upstream.Respond = c =>
        {
            if (c.Uri.Host != "up1.test") return CompletedSse(fallback);
            return failure switch
            {
                "http_500" => FakeUpstream.Json(500, "{}"),
                "empty_stream" => EmptySse(),
                _ => CompletedSse(Sse("""{"error":{"message":"boom","code":"upstream_stream_error"}}""")),
            };
        };

        var (response, reader) = await StartStreamAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var events = await DrainAsync(reader);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(["up1.test", "up2.test"], Upstream.Requests.Select(r => r.Uri.Host).ToArray());
        Assert.Equal("fallback", ChunkContent(events[0].Json));
        Assert.Equal(Alias, (string?)events[0].Json!["model"]);
        Assert.True(events[^1].IsDone);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Ok, 2, true, 200), (log.Status, log.Attempts, log.FallbackUsed, log.HttpStatus));
    }

    [Theory]
    [InlineData(429, "upstream_rate_limited", 429)]
    [InlineData(401, "provider_auth_failed", 502)]
    public async Task Upstream_status_errors_on_a_stream_request_keep_the_json_mapping(int upstreamStatus, string code, int expectedStatus)
    {
        var s = await SetupAsync(alias: Alias);
        Upstream.Respond = _ =>
        {
            var response = FakeUpstream.Json(upstreamStatus, "{}");
            if (upstreamStatus == 429) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        var response = await PostAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(code, ErrorCode(await BodyAsync(response)));
        if (upstreamStatus == 429) Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, upstreamStatus, expectedStatus), (log.Status, log.UpstreamStatus, log.HttpStatus));
    }

    [Fact]
    public async Task Upstream_that_ignores_stream_returns_buffered_json()
    {
        var s = await SetupAsync(alias: Alias);
        Upstream.Respond = _ => FakeUpstream.Json(200, FakeUpstream.OkBody);

        var response = await PostAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Alias, (string?)(await BodyAsync(response))["model"]);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Ok, 10, 20, 0.00005m), (log.Status, log.InputTokens, log.OutputTokens, log.Cost));
    }

    // --- kegagalan setelah commit -----------------------------------------------------------

    [Fact]
    public async Task Mid_stream_failure_sends_one_error_event_and_releases_the_slot()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:MaxConcurrentPerTenant", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        await AddSecondRouteAsync(s);
        var body = new GatedBody();
        Upstream.Respond = c => c.Uri.Host == "up1.test" ? Sse(body) : CompletedSse(Sse(Delta("c9", "fallback"), "[DONE]"));
        body.Write(Sse(Delta("c1", "ha")));

        var (response, reader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ha", ChunkContent((await ReadEventAsync(reader))!.Json));

        body.Fail(new IOException("koneksi upstream terputus"));
        var rest = await DrainAsync(reader);

        var error = Assert.Single(rest);
        Assert.Equal("upstream_stream_interrupted", ErrorCode(error.Json!));
        Assert.False(error.IsDone);
        Assert.Equal(["up1.test"], Upstream.Requests.Select(r => r.Uri.Host).ToArray()); // tidak ada fallback setelah commit

        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 200, "upstream_stream_interrupted", 1, false),
            (log.Status, log.HttpStatus, log.DeniedReason, log.Attempts, log.FallbackUsed));

        // Slot tenant dilepas walau stream berakhir dengan error.
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c2", "ok"), Finish("c3"), "[DONE]"));
        var (next, nextReader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal("ok", ChunkContent((await ReadEventAsync(nextReader))!.Json));
        await DrainAsync(nextReader);
    }

    [Fact]
    public async Task Idle_upstream_triggers_upstream_stream_timeout()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:UpstreamTimeoutSeconds", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => Sse(body);
        body.Write(Sse(Delta("c1", "ha")));

        var (response, reader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ha", ChunkContent((await ReadEventAsync(reader))!.Json));

        var rest = await DrainAsync(reader); // upstream diam: batas diam 1 detik adalah yang diuji

        Assert.Equal("upstream_stream_timeout", ErrorCode(Assert.Single(rest).Json!));
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 200, "upstream_stream_timeout"), (log.Status, log.HttpStatus, log.DeniedReason));
    }

    [Fact]
    public async Task Trickling_upstream_hits_the_total_duration_cap()
    {
        using var factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Proxy:MaxStreamSeconds", "1");
            b.UseSetting("Proxy:UpstreamTimeoutSeconds", "5");
        });
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => Sse(body);
        body.Write(Sse(Delta("c1", "ha")));

        using var trickle = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var index = 2;
            try
            {
                while (true)
                {
                    await Task.Delay(250, trickle.Token);
                    body.Write(Sse(Delta($"c{index++}", "x"))); // tetap mengalir: batas diam tidak pernah tercapai
                }
            }
            catch (OperationCanceledException) { }
        });

        var (response, reader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await DrainAsync(reader);
        trickle.Cancel();
        await writer;

        Assert.Equal("upstream_stream_timeout", ErrorCode(events[^1].Json!));
        Assert.DoesNotContain(events, e => e.IsDone);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 200, "upstream_stream_timeout"), (log.Status, log.HttpStatus, log.DeniedReason));
    }

    [Fact]
    public async Task Oversized_stream_is_aborted_with_upstream_response_too_large()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:MaxResponseBytes", "1024"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => Sse(body);
        body.Write(Sse(Delta("c1", "ha")));                    // event pertama kecil supaya stream sudah commit
        body.Write(Sse(Delta("c2", new string('x', 4000))));   // melewati batas byte
        body.Complete();

        var (response, reader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await DrainAsync(reader);

        Assert.Equal(2, events.Count);
        Assert.Equal("ha", ChunkContent(events[0].Json));
        Assert.Equal("upstream_response_too_large", ErrorCode(events[1].Json!));
        Assert.DoesNotContain(events, e => e.IsDone);
        Assert.DoesNotContain("xxxx", events[1].Raw); // potongan besar tidak pernah sampai ke klien
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 200, "upstream_response_too_large"), (log.Status, log.HttpStatus, log.DeniedReason));
    }

    // --- klien putus & slot konkurensi ------------------------------------------------------

    [Fact]
    public async Task Client_disconnect_records_499_releases_the_slot_and_cancels_the_upstream()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:MaxConcurrentPerTenant", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => Sse(body);
        body.Write(Sse(Delta("c1", "ha")));

        using var cts = new CancellationTokenSource();
        var (response, reader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"), cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await ReadEventAsync(reader));

        cts.Cancel(); // klien memutus koneksi di tengah stream
        response.Dispose();
        reader.Dispose();

        Assert.True(await EventuallyAsync(async () =>
        {
            await using var ctx = Db.NewContext(s.TenantId);
            return await ctx.UsageLogs.AnyAsync(l => l.Status == UsageStatuses.Error);
        }), "baris usage 499 tidak muncul setelah klien memutus koneksi");

        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 499, 1), (log.Status, log.HttpStatus, log.Attempts));
        Assert.Equal((0, 0), (log.InputTokens, log.OutputTokens)); // usage belum dilaporkan upstream (batas yang diketahui)

        await body.Disposed.WaitAsync(TimeSpan.FromSeconds(15)); // stream upstream dibatalkan/ditutup

        Upstream.Respond = _ => CompletedSse(Sse(Delta("c9", "ok"), Finish("c10"), "[DONE]"));
        var (next, nextReader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        await DrainAsync(nextReader);
    }

    [Fact]
    public async Task The_tenant_slot_is_held_for_the_whole_stream()
    {
        using var factory = Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:MaxConcurrentPerTenant", "1"));
        using var client = factory.CreateClient();
        var s = await SetupAsync(alias: Alias);
        var body = new GatedBody();
        Upstream.Respond = _ => Sse(body);
        body.Write(Sse(Delta("c1", "ha")));

        var (_, reader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal("ha", ChunkContent((await ReadEventAsync(reader))!.Json));

        var denied = await PostAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal((HttpStatusCode)429, denied.StatusCode);
        Assert.Equal("concurrency_limit_exceeded", ErrorCode(await BodyAsync(denied)));
        Assert.Equal(TimeSpan.FromSeconds(1), denied.Headers.RetryAfter?.Delta);

        body.Write(Sse(Finish("c2"), "[DONE]"));
        body.Complete();
        await DrainAsync(reader);

        Upstream.Respond = _ => CompletedSse(Sse(Delta("c3", "again"), "[DONE]"));
        var (next, nextReader) = await StartStreamAsync(client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal("again", ChunkContent((await ReadEventAsync(nextReader))!.Json));
        await DrainAsync(nextReader);
    }

    // --- penanganan baris & isi tersimpan ---------------------------------------------------

    [Fact]
    public async Task Non_data_lines_pass_through_and_a_missing_done_is_appended()
    {
        var s = await SetupAsync(alias: Alias);
        var delta = Delta("c1", "ha");
        var finish = Finish("c2");
        Upstream.Respond = _ => CompletedSse(": ping\n"
            + "event: update\n"
            + "id: 7\n"
            + "retry: 100\n"
            + "data: not json\n"
            + "data: " + delta + "\n\n"
            + "data: " + finish + "\n\n"); // upstream menutup tanpa [DONE]

        var response = await PostAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains(": ping\n", raw);
        Assert.Contains("event: update\n", raw);
        Assert.Contains("id: 7\n", raw);
        Assert.Contains("retry: 100\n", raw);
        Assert.Contains("data: not json\n", raw);       // data non-JSON diteruskan apa adanya
        Assert.Contains("\"model\":\"gpt-stream\"", raw);
        Assert.Contains("\"content\":\"ha\"", raw);
        Assert.EndsWith("data: [DONE]\n\n", raw);       // finish_reason terlihat -> [DONE] ditambahkan
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Ok, "stop"), (log.Status, log.FinishReason));
    }

    [Fact]
    public async Task Upstream_closing_without_finish_reason_is_reported_as_interrupted()
    {
        var s = await SetupAsync(alias: Alias);
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c1", "ha"))); // tanpa finish_reason dan tanpa [DONE]

        var (_, reader) = await StartStreamAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        var events = await DrainAsync(reader);

        Assert.Equal(2, events.Count);
        Assert.Equal("ha", ChunkContent(events[0].Json));
        Assert.Equal("upstream_stream_interrupted", ErrorCode(events[1].Json!));
        Assert.False(events[1].IsDone);
        var log = await LogAsync(s.TenantId);
        Assert.Equal((UsageStatuses.Error, 200, "upstream_stream_interrupted"), (log.Status, log.HttpStatus, log.DeniedReason));
    }

    [Fact]
    public async Task Streamed_response_content_is_stored_when_log_content_is_enabled()
    {
        var s = await SetupAsync(alias: Alias);
        await WithTenantAsync(s.TenantId, async (prov, _) =>
        {
            await prov.UpdateProjectAsync(s.ProjectId, new ProjectPatch(LogContent: true), default);
            return 0;
        });
        Upstream.Respond = _ => CompletedSse(Sse(Delta("c1", "ha"), Delta("c2", "lo"), Finish("c3"), UsageChunk, "[DONE]"));

        var (_, reader) = await StartStreamAsync(Client, s.ApiKey, Chat(Alias, ",\"stream\":true"));
        await DrainAsync(reader);

        var log = await LogAsync(s.TenantId);
        await using var ctx = Db.NewContext(s.TenantId);
        var stored = await ctx.Set<RequestBody>().SingleAsync(b => b.UsageLogId == log.Id);
        Assert.Contains("\"streamed\":true", stored.ResponseJson!);
        Assert.Contains("\"content\":\"halo\"", stored.ResponseJson!);
        Assert.Contains("\"finish_reason\":\"stop\"", stored.ResponseJson!);
        Assert.Contains("\"prompt_tokens\":10", stored.ResponseJson!);
    }

    /// <summary>
    /// Body respons upstream yang dikendalikan test: potongan ditulis kapan saja, ditutup, atau digagalkan.
    /// ReadAsync menghormati token pembatalan sehingga klien putus dan timeout bisa diuji tanpa waktu tetap.
    /// </summary>
    private sealed class GatedBody : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _pending = [];
        private int _offset;
        private Exception? _failure;

        /// <summary>Selesai ketika stream ini ditutup gateway (mis. klien putus atau stream berakhir).</summary>
        public Task Disposed => _disposed.Task;

        public void Write(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void Complete() => _chunks.Writer.TryComplete();

        /// <summary>Gagalkan pembacaan berikutnya (mis. koneksi upstream terputus di tengah stream).</summary>
        public void Fail(Exception failure)
        {
            _failure = failure;
            _chunks.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _pending.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    if (_failure is { } failure) throw failure;
                    return 0;
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

/// <summary>
/// Batas byte <see cref="LimitedReadStream"/>: tepat sebesar batas masih lolos, satu byte setelahnya melempar.
/// </summary>
public class LimitedReadStreamTests
{
    [Fact]
    public void Exactly_the_limit_is_read_and_one_byte_more_throws()
    {
        using var stream = new LimitedReadStream(new MemoryStream(new byte[16]), 4);
        var buffer = new byte[4];

        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Throws<ResponseTooLargeException>(() => stream.Read(buffer, 0, 1));
    }

    [Fact]
    public async Task Async_reads_count_towards_the_same_limit()
    {
        using var stream = new LimitedReadStream(new MemoryStream(new byte[16]), 4);
        var buffer = new byte[4];

        Assert.Equal(4, await stream.ReadAsync(buffer));
        await Assert.ThrowsAsync<ResponseTooLargeException>(async () => await stream.ReadExactlyAsync(buffer));
    }
}
