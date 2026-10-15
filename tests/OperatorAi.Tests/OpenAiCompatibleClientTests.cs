using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

public class OpenAiCompatibleClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? RequestPath, RequestBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return respond(request, RequestBody);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Body respons yang tidak pernah mengirim byte dan tidak pernah selesai; hanya berhenti saat dibatalkan.</summary>
    private sealed class StalledBody : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Handler pipa produksi (<c>AddAiClient</c>): panggilan ke-n dijawab, null dibiarkan macet tanpa header.</summary>
    private sealed class StallHandler(Func<int, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (respond(Interlocked.Increment(ref _calls)) is { } response) return response;
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("tidak tercapai");
        }
    }

    /// <summary>Klien lewat pipeline sungguhan supaya generator timeout membaca batas per panggilan dari request.</summary>
    private static (IAiClient Client, StallHandler Handler) ResilientClient(Func<int, HttpResponseMessage?> respond)
    {
        var handler = new StallHandler(respond);
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AiOptions>(o =>
        {
            o.Provider = "OpenAiCompatible";
            o.BaseUrl = "http://gateway.test/v1/";
            o.MessagesPath = "chat/completions";
            o.ApiKey = "gw_test";
        });
        services.AddAiClient();
        // Handler utama diganti stub; retry/timeout/breaker tetap pipeline produksi.
        services.AddHttpClient<IAiClient, OpenAiCompatibleClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        return (services.BuildServiceProvider().GetRequiredService<IAiClient>(), handler);
    }

    private static (OpenAiCompatibleClient Client, StubHandler Handler) NewClient(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test/v1/") };
        var options = Options.Create(new AiOptions
        {
            Provider = "OpenAiCompatible", MaxTokens = 123,
            BaseUrl = "http://gateway.test/v1/", MessagesPath = "chat/completions",
        });
        return (new OpenAiCompatibleClient(http, options, NullLogger<OpenAiCompatibleClient>.Instance), handler);
    }

    private static readonly AiRequest Request = new("m-1", Guid.Parse("12fbad3a-9b07-463f-a8d5-56409fd7f7ed"), "SYS", [new AiMessage("user", "halo"), new AiMessage("assistant", "hai"), new AiMessage("user", "lagi")], 123);

    [Fact]
    public async Task Reads_public_text_and_usage_without_exposing_private_reasoning()
    {
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"role":"assistant","content":"Bandung","reasoning_content":"mikir"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":56,"completion_tokens":69}}
            """));

        var response = await client.CompleteAsync(Request, CancellationToken.None);

        Assert.Equal(("Bandung", "stop", 56, 69), (response.Text, response.FinishReason, response.PromptTokens, response.CompletionTokens));
        Assert.True(response.UsageAvailable);
        Assert.DoesNotContain("mikir", response.Text);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task Null_content_with_finish_reason_length_is_returned_as_empty_text_for_the_caller_to_judge()
    {
        // Seluruh token habis untuk reasoning (diamati di Fase 0): content null, finish_reason "length".
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"role":"assistant","content":null,"reasoning_content":"..."},"finish_reason":"length"}],"usage":{"prompt_tokens":5,"completion_tokens":64}}
            """));

        var response = await client.CompleteAsync(Request, CancellationToken.None);

        Assert.Equal(("", "length", 5, 64), (response.Text, response.FinishReason, response.PromptTokens, response.CompletionTokens));
    }

    [Fact]
    public async Task Missing_usage_is_an_explicit_unknown_lower_bound()
    {
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));

        var response = await client.CompleteAsync(Request, CancellationToken.None);
        Assert.Equal(("ok", "stop", 0, 0), (response.Text, response.FinishReason, response.PromptTokens, response.CompletionTokens));
        Assert.False(response.UsageAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(777)]
    public async Task Output_budget_is_sent_only_when_the_snapshot_has_one(int? budget)
    {
        var (client, handler) = NewClient((_, _) => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));

        await client.CompleteAsync(Request with { MaxTokens = budget }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        // Tanpa anggaran field-nya absen sama sekali (bukan null/0): gateway yang menentukan batas keluaran.
        Assert.Equal(budget, body.RootElement.TryGetProperty("max_tokens", out var sent) ? sent.GetInt32() : (int?)null);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "model_not_allowed", true)]
    [InlineData(HttpStatusCode.NotFound, "model_not_found", true)]
    [InlineData(HttpStatusCode.Forbidden, "budget_exceeded", false)]
    [InlineData(HttpStatusCode.NotFound, "endpoint_not_found", false)]
    [InlineData(HttpStatusCode.BadRequest, "model_not_allowed", false)]
    public async Task Only_structured_model_revocation_is_terminal(HttpStatusCode status, string code, bool unavailable)
    {
        var (client, _) = NewClient((_, _) => Json(status, JsonSerializer.Serialize(new { error = new { code } })));
        var exception = await Assert.ThrowsAnyAsync<AiException>(() => client.CompleteAsync(Request, CancellationToken.None));
        Assert.Equal(unavailable, exception is AiModelUnavailableException);
    }

    [Fact]
    public async Task Per_request_limits_control_the_http_attempt_total_and_body_budget()
    {
        // Snapshot A: kedua batas 1 dtk; satu permintaan berhenti sebelum retry, bukan menunggu baseline 60/120 dtk.
        var (clientA, stalled) = ResilientClient(_ => null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<AiException>(() => clientA.CompleteAsync(Request with
        {
            Limits = new AiRuntimeLimits { AttemptTimeoutSeconds = 1, TotalRequestTimeoutSeconds = 1, StreamFirstByteSeconds = 1 },
        }, CancellationToken.None));

        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 5);
        Assert.Equal(1, stalled.Calls);

        // Header datang seketika, body tidak pernah selesai: timeout handler resilience sudah berakhir, jadi hanya batas
        // total snapshot di klien yang bisa menghentikan pembacaan body (tanpa ini panggilan menggantung selamanya).
        var (clientBody, bodyCalls) = ResilientClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledBody()),
        });
        clock.Restart();
        await Assert.ThrowsAsync<AiException>(() => clientBody.CompleteAsync(Request with
        {
            Limits = new AiRuntimeLimits { AttemptTimeoutSeconds = 1, TotalRequestTimeoutSeconds = 2, StreamFirstByteSeconds = 1 },
        }, CancellationToken.None));

        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 6);
        Assert.Equal(1, bodyCalls.Calls);

        // Snapshot B: attempt 1 dtk, total 30 dtk → percobaan pertama timeout lalu diulang handler resilience standar.
        var (clientB, retried) = ResilientClient(call => call < 2 ? null : Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        clock.Restart();
        var response = await clientB.CompleteAsync(Request with
        {
            Limits = new AiRuntimeLimits { AttemptTimeoutSeconds = 1, TotalRequestTimeoutSeconds = 30, StreamFirstByteSeconds = 1 },
        }, CancellationToken.None);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, retried.Calls);                                     // attempt 1 dtk snapshot, bukan attempt 60 dtk
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.9, 25);
    }

    [Fact]
    public async Task Obsolete_worker_budget_values_cannot_prevent_the_snapshot_deadline_from_running()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:BaseUrl"] = "http://gateway.test/v1/", ["Ai:MessagesPath"] = "chat/completions", ["Ai:ApiKey"] = "gw_test",
            ["Ai:AttemptTimeoutSeconds"] = "not-a-number", ["Ai:StreamFirstByteSeconds"] = "not-a-number",
            ["Operator:MaxToolLoops"] = "not-a-number", ["Upload:MaxBytes"] = "not-a-number",
            ["Document:WatchdogSeconds"] = "not-a-number",
        }).Build();
        var handler = new StallHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledBody()),
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkerOptions(config);
        services.AddAiClient();
        services.AddHttpClient<IAiClient, OpenAiCompatibleClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAiClient>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<AiException>(() => client.CompleteAsync(Request with
        {
            Limits = new AiRuntimeLimits { AttemptTimeoutSeconds = 1, TotalRequestTimeoutSeconds = 2, StreamFirstByteSeconds = 1 },
        }, CancellationToken.None));
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 6);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Unreadable_success_body_and_network_failure_both_become_AiException()
    {
        var (garbled, _) = NewClient((_, _) => Json(HttpStatusCode.OK, """{"choices":[]}"""));
        await Assert.ThrowsAsync<AiException>(() => garbled.CompleteAsync(Request, CancellationToken.None));

        var (down, _) = NewClient((_, _) => throw new HttpRequestException("connection refused"));
        var ex = await Assert.ThrowsAsync<AiException>(() => down.CompleteAsync(Request, CancellationToken.None));
        Assert.Contains("connection refused", ex.Message);
    }

    [Fact]
    public async Task Tool_calls_are_parsed_and_the_raw_assistant_message_is_kept_for_replay()
    {
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"role":"assistant","content":"","reasoning_content":"mikir",
              "tool_calls":[{"index":0,"id":"call_9","type":"function","function":{"name":"cek_status","arguments":"{}"}}]},
              "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}
            """));

        var response = await client.CompleteAsync(Request, CancellationToken.None);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(("call_9", "cek_status", "{}"), (call.Id, call.Name, call.Arguments));
        Assert.Equal("tool_calls", response.FinishReason);
        // Pesan yang disimpan untuk dikirim ulang: tool_calls utuh (termasuk "index") dan reasoning_content.
        var replay = response.Message.ToJson();
        Assert.Equal(("assistant", "mikir", 0), (replay["role"]!.GetValue<string>(), replay["reasoning_content"]!.GetValue<string>(),
            replay["tool_calls"]![0]!["index"]!.GetValue<int>()));
    }

    [Theory]
    [InlineData("""{"prompt_tokens":-1,"completion_tokens":4}""")]
    [InlineData("""{"prompt_tokens":3,"completion_tokens":-4}""")]
    [InlineData("""{"prompt_tokens":"unknown","completion_tokens":4}""")]
    public async Task Invalid_upstream_usage_is_not_counted_as_success(string usage)
    {
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.OK,
            $$"""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{{usage}}}"""));
        await Assert.ThrowsAsync<AiException>(() => client.CompleteAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Partial_usage_keeps_known_tokens_without_claiming_a_complete_total()
    {
        var (client, _) = NewClient((_, _) => Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":17}}"""));
        var response = await client.CompleteAsync(Request, CancellationToken.None);
        Assert.Equal(17, response.PromptTokens);
        Assert.False(response.UsageAvailable);
    }
}
