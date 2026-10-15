using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Options;

namespace OperatorAi.Tests;

/// <summary>
/// Perilaku jalur stream yang terlihat pemanggil: framing SSE, delta publik yang mengalir selagi body terbuka,
/// usage yang tetap tercatat saat gagal, batas body, dan pembatalan.
/// </summary>
public class StreamingClientTests
{
    /// <summary>Body respons yang ditulis test potong demi potong; tanpa <see cref="End"/> stream tetap terbuka.</summary>
    private sealed class PushBody : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;

        public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

        public void Write(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

        public void End() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(ct)) return 0;
                _current = await _chunks.Reader.ReadAsync(ct);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StreamHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static (OpenAiCompatibleClient Client, StreamHandler Handler) NewClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond, Action<AiOptions>? tweak = null)
    {
        var handler = new StreamHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test/v1/") };
        var options = new AiOptions
        {
            Provider = "OpenAiCompatible", MaxTokens = 123,
            BaseUrl = "http://gateway.test/v1/", MessagesPath = "chat/completions",
        };
        tweak?.Invoke(options);
        return (new OpenAiCompatibleClient(http, Options.Create(options), NullLogger<OpenAiCompatibleClient>.Instance), handler);
    }

    private static HttpResponseMessage Sse(PushBody body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static readonly AiRequest Request = new("m-1", Guid.Parse("12fbad3a-9b07-463f-a8d5-56409fd7f7ed"), "SYS",
        [new AiMessage("user", "halo")], 123, new JsonArray(new JsonObject
        {
            ["type"] = "function", ["function"] = new JsonObject { ["name"] = "cek_status" },
        }), "conversation=12fbad3a;queueItem=7");

    /// <summary>Baris data SSE untuk satu chunk; <c>finish_reason</c> null tetap dikirim seperti gateway.</summary>
    private static string Chunk(JsonObject delta, string? finish = null)
    {
        var choices = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish });
        return Line(new JsonObject { ["choices"] = choices });
    }

    private static string UsageChunk(int prompt, int completion)
    {
        var usage = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion };
        return Line(new JsonObject { ["choices"] = new JsonArray(), ["usage"] = usage });
    }

    private static string Line(JsonObject payload) => $"data: {payload.ToJsonString()}\n\n";

    private const string Done = "data: [DONE]\n\n";

    /// <summary>Penyedia macet: header tidak pernah datang sampai request dibatalkan.</summary>
    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("tidak tercapai");
        }
    }

    [Fact]
    public async Task A_provider_that_sends_no_first_byte_fails_fast_with_a_dedicated_exception()
    {
        var options = new AiOptions
        {
            Provider = "OpenAiCompatible", BaseUrl = "http://gateway.test/v1/", MessagesPath = "chat/completions",
            StreamFirstByteSeconds = 1,
        };
        var client = new OpenAiCompatibleClient(new HttpClient(new StalledHandler()) { BaseAddress = new Uri(options.BaseUrl) },
            Options.Create(options), NullLogger<OpenAiCompatibleClient>.Instance);
        var deltas = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var timeout = await Assert.ThrowsAsync<AiFirstByteTimeoutException>(() =>
            client.CompleteStreamingAsync(Request, (text, _) => { deltas.Add(text); return Task.CompletedTask; }, CancellationToken.None));

        Assert.Equal(1, timeout.Seconds);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.9, 10);   // cepat, bukan menunggu batas attempt/total HTTP
        Assert.Empty(deltas);

        // Pembatalan dari pemanggil (mis. worker dihentikan) bukan keterlambatan penyedia: tetap cancellation biasa.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask, cancel.Token));
    }

    [Fact]
    public async Task The_first_byte_limit_stops_applying_once_the_stream_has_started()
    {
        var body = new PushBody();
        var (client, _) = NewClient(_ => Sse(body), o => o.StreamFirstByteSeconds = 1);

        var running = client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask, CancellationToken.None);
        // Header sudah tiba; teks pertama datang lebih lambat dari batas byte pertama dan tidak boleh dibatalkan.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        body.Write(Chunk(new JsonObject { ["content"] = "terlambat tapi sah" }, finish: "stop"));
        body.Write(Done);
        body.End();

        var response = await running;
        Assert.Equal("terlambat tapi sah", response.Text);
    }

    [Fact]
    public async Task Streams_public_content_early_and_reconstructs_the_full_response()
    {
        var body = new PushBody();
        var deltas = new List<string>();
        // Hanya potongan pertama yang ada sejak awal; sisanya baru ditulis dari dalam callback. Callback karena itu
        // harus jalan selagi body masih terbuka, bukan setelah seluruh jawaban selesai dibaca.
        body.Write(Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "Halo", ["reasoning_content"] = "mikir awal" }));

        var (client, _) = NewClient(_ => Sse(body));
        var response = await client.CompleteStreamingAsync(Request, async (text, _) =>
        {
            deltas.Add(text);
            if (text != "Halo") return;
            body.Write(Chunk(new JsonObject { ["reasoning_content"] = " lanjut mikir" }));
            body.Write(Chunk(new JsonObject { ["content"] = ", apa" }));
            body.Write(Chunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["id"] = "call_7", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "cek_status", ["arguments"] = "{\"queue" },
                }),
            }));
            body.Write(Chunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["function"] = new JsonObject { ["arguments"] = "ItemId\":12}" },
                }),
            }, finish: "tool_calls"));
            body.Write(UsageChunk(41, 7));
            body.Write(Done);
            await Task.Yield();
        }, CancellationToken.None);
        body.End();

        Assert.Equal(["Halo", ", apa"], deltas);
        Assert.Equal(("Halo, apa", "tool_calls", 41, 7, true),
            (response.Text, response.FinishReason, response.PromptTokens, response.CompletionTokens, response.UsageAvailable));
        // Reasoning dan fragmen argumen tool tidak pernah sampai ke pemanggil, tapi tetap utuh untuk dikirim ulang.
        Assert.Equal("mikir awal lanjut mikir", response.Message.ReasoningContent);
        Assert.DoesNotContain("mikir", string.Concat(deltas));
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(("call_7", "cek_status", "{\"queueItemId\":12}"), (call.Id, call.Name, call.Arguments));
        var replay = response.Message.ToJson();
        var replayedCall = replay["tool_calls"]![0]!;
        Assert.Equal((0, "function", "cek_status", "{\"queueItemId\":12}"),
            (replayedCall["index"]!.GetValue<int>(), replayedCall["type"]!.GetValue<string>(),
                replayedCall["function"]!["name"]!.GetValue<string>(), replayedCall["function"]!["arguments"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Framing_accepts_crlf_multiline_data_comments_and_utf8_split_across_reads()
    {
        var body = new PushBody();
        // data multiline (dua baris data digabung "\n") tetap satu payload JSON, komentar dan event: diabaikan,
        // dan baris CRLF harus dipisah tanpa menyisakan '\r' di payload.
        var text = string.Concat(
            ": heartbeat\n",
            "\r\n",
            "event: message\r\n",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"A🚀B\"}}]\r\n",
            "data: }\r\n",
            "\r\n",
            Chunk(new JsonObject { ["content"] = "çok" }, finish: "stop").TrimEnd('\n') + "\r\n\r\n",
            Done);
        // Potong di tengah urutan UTF-8 emoji: decoder harus menyambung karakter, bukan mengirim pengganti.
        var bytes = Encoding.UTF8.GetBytes(text);
        var emoji = Encoding.UTF8.GetBytes("🚀");
        var mid = IndexOf(bytes, emoji) + 2;
        Assert.True(mid > 2);
        body.Write(bytes[..mid]);
        body.Write(bytes[mid..]);
        body.End();

        var (client, _) = NewClient(_ => Sse(body));
        var deltas = new List<string>();
        var response = await client.CompleteStreamingAsync(Request, (delta, _) =>
        {
            deltas.Add(delta);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(["A🚀B", "çok"], deltas);
        Assert.Equal(("A🚀Bçok", "stop"), (response.Text, response.FinishReason));
        // Tanpa chunk usage: belum diketahui, bukan klaim nol token.
        Assert.False(response.UsageAvailable);
        Assert.Equal(0, response.PromptTokens + response.CompletionTokens);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Theory]
    [InlineData(true)]  // EOF tanpa [DONE]
    [InlineData(false)] // [DONE] tanpa finish_reason
    public async Task Missing_done_or_finish_is_not_a_successful_truncated_answer(bool withoutDone)
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = "Halo" }));
        body.Write(withoutDone ? Chunk(new JsonObject(), finish: "stop") : Done);
        body.End();
        var deltas = new List<string>();

        var (client, _) = NewClient(_ => Sse(body));
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (delta, _) =>
        {
            deltas.Add(delta);
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Contains(withoutDone ? "[DONE]" : "finish_reason", ex.Message);
        Assert.False(ex.UsageAvailable);
        Assert.False(ex.RequestCanceled);
        Assert.Equal(["Halo"], deltas); // potongan yang sudah terlihat tetap terlihat
    }

    [Fact]
    public async Task Idle_gap_over_the_limit_aborts_the_body()
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = "Halo" })); // lalu diam: stream sengaja tidak ditutup

        var (client, _) = NewClient(_ => Sse(body), o => o.StreamIdleSeconds = 1);
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        Assert.Contains("diam", ex.Message);
        Assert.False(ex.UsageAvailable);
        Assert.False(ex.RequestCanceled);
    }

    [Fact]
    public async Task Total_duration_over_the_limit_aborts_the_body()
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = "Halo" }));

        var (client, _) = NewClient(_ => Sse(body), o => o.MaxStreamSeconds = 1);
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        Assert.Contains("durasi", ex.Message);
        Assert.False(ex.RequestCanceled);
    }

    [Fact]
    public async Task Body_over_the_byte_limit_aborts_before_anything_is_parsed()
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = new string('x', 400) }, finish: "stop") + Done);
        body.End();
        var deltas = new List<string>();

        var (client, _) = NewClient(_ => Sse(body), o => o.MaxStreamBytes = 200);
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (delta, _) =>
        {
            deltas.Add(delta);
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Contains("byte", ex.Message);
        Assert.Empty(deltas);
    }

    [Fact]
    public async Task Per_request_limits_control_the_stream_budget_of_the_same_client()
    {
        // Snapshot A: batas diam 1 dtk → stream yang berhenti bicara digagalkan jauh sebelum default proses.
        var idleBody = new PushBody();
        idleBody.Write(Chunk(new JsonObject { ["content"] = "Halo" })); // lalu diam: stream sengaja tidak ditutup
        // Snapshot B: diam 60 dtk tapi durasi total 1 dtk → batas durasi snapshot yang berlaku.
        var durationBody = new PushBody();
        durationBody.Write(Chunk(new JsonObject { ["content"] = "Halo" }));
        // Snapshot C: body dibatasi 1 KiB → stream lebih besar digagalkan sebelum chunk apa pun sampai ke pemanggil.
        var bytesBody = new PushBody();
        bytesBody.Write(Chunk(new JsonObject { ["content"] = new string('x', 2000) }, finish: "stop") + Done);
        bytesBody.End();

        // Satu klien, opsi proses default (diam 60 dtk, durasi 900 dtk, body 8 MiB): tiap panggilan memakai snapshotnya.
        var bodies = new Queue<PushBody>([idleBody, durationBody, bytesBody]);
        var (client, _) = NewClient(_ => Sse(bodies.Dequeue()));

        var idle = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request with
        {
            Limits = new AiRuntimeLimits { StreamIdleSeconds = 1 },
        }, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("diam", idle.Message);

        var duration = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request with
        {
            Limits = new AiRuntimeLimits { StreamIdleSeconds = 60, MaxStreamSeconds = 1 },
        }, (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("durasi", duration.Message);

        var deltas = new List<string>();
        var bytes = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request with
        {
            Limits = new AiRuntimeLimits { MaxStreamBytes = 1024 },
        }, (delta, _) => { deltas.Add(delta); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Contains("1024 byte", bytes.Message);
        Assert.Empty(deltas);
    }

    [Fact]
    public async Task Single_line_over_the_limit_aborts_the_body()
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = new string('x', 200) }, finish: "stop") + Done);
        body.End();

        var (client, _) = NewClient(_ => Sse(body), o => o.MaxStreamEventBytes = 64);
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        Assert.Contains("Baris SSE", ex.Message);
    }

    [Fact]
    public async Task Multiline_event_over_the_limit_aborts_before_dispatch()
    {
        var body = new PushBody();
        // Tiap baris di bawah batas, tapi satu event (beberapa baris data sampai baris kosong) melewatinya.
        body.Write("data: {\"choices\":[{\"index\":0,\n");
        body.Write("data: \"delta\":{\"content\":\"Halo\"},\n");
        body.Write("data: \"finish_reason\":\"stop\"}]}\n");
        body.Write("\n");
        body.Write(Done);
        body.End();
        var deltas = new List<string>();

        var (client, _) = NewClient(_ => Sse(body), o => o.MaxStreamEventBytes = 80);
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (delta, _) =>
        {
            deltas.Add(delta);
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Contains("Event SSE", ex.Message);
        Assert.Empty(deltas); // payload yang belum lengkap tidak pernah dikirim sebagai teks
    }

    [Fact]
    public async Task Malformed_usage_chunk_keeps_the_tokens_already_received()
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = "Halo" }));
        body.Write(UsageChunk(12, 3));
        body.Write("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":\"banyak\",\"completion_tokens\":4}}\n\n");
        body.End();

        var (client, _) = NewClient(_ => Sse(body));
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        // Usage sah yang sudah diterima tidak diganti nol oleh chunk rusak berikutnya.
        Assert.Equal((12, 3, true), (ex.PromptTokens, ex.CompletionTokens, ex.UsageAvailable));
        Assert.False(ex.RequestCanceled);
    }

    [Fact]
    public async Task Malformed_tool_fragment_shape_is_accounted_as_a_stream_failure()
    {
        var body = new PushBody();
        body.Write(UsageChunk(5, 2));
        // "function" bukan objek: bentuk yang tidak bisa diakumulasi tanpa melempar error mentah.
        body.Write("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":\"cek_status\"}]}}]}\n\n");
        body.Write(Done);
        body.End();

        var (client, _) = NewClient(_ => Sse(body));
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        // Exception mentah dari bentuk fragmen yang menyimpang tidak boleh lolos: tetap AiStreamException + usage.
        Assert.Equal((5, 2, true), (ex.PromptTokens, ex.CompletionTokens, ex.UsageAvailable));
        Assert.False(ex.RequestCanceled);
    }

    [Fact]
    public async Task Midstream_error_after_usage_keeps_the_observed_tokens_and_does_not_retry()
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = "Halo" }));
        body.Write(UsageChunk(12, 3));
        // Bentuk kegagalan pasca-commit dari gateway: satu event error lalu koneksi ditutup tanpa [DONE].
        var error = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["type"] = "upstream_error", ["code"] = "upstream_stream_interrupted",
                ["message"] = "The provider closed the stream before it finished.",
            },
        };
        body.Write(Line(error));
        body.End();

        var (client, handler) = NewClient(_ => Sse(body));
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        Assert.Contains("upstream_stream_interrupted", ex.Message);
        // Token yang benar-benar diterima dicatat; nilai lama tidak diganti nol dan tidak diestimasi.
        Assert.Equal((12, 3, true), (ex.PromptTokens, ex.CompletionTokens, ex.UsageAvailable));
        Assert.False(ex.RequestCanceled);
        Assert.Equal(1, handler.Calls); // kegagalan body tidak memicu retry request baru
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_reported_with_the_usage_already_received()
    {
        using var cts = new CancellationTokenSource();
        var body = new PushBody();
        body.Write(Chunk(new JsonObject { ["content"] = "Halo" }));
        body.Write(UsageChunk(9, 4));
        body.Write(Chunk(new JsonObject { ["content"] = " lagi" }));

        var (client, handler) = NewClient(_ => Sse(body));
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (delta, _) =>
        {
            if (delta == " lagi") cts.Cancel(); // setelah usage terlihat, sebelum [DONE]
            return Task.CompletedTask;
        }, cts.Token));

        Assert.True(ex.RequestCanceled);
        Assert.Equal((9, 4, true), (ex.PromptTokens, ex.CompletionTokens, ex.UsageAvailable));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Truncated_tool_arguments_fail_before_any_call_can_be_executed()
    {
        var body = new PushBody();
        body.Write(Chunk(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["id"] = "call_1", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "cek_status", ["arguments"] = "{\"queueItem" },
            }),
        }, finish: "tool_calls"));
        body.Write(Done);
        body.End();

        var (client, _) = NewClient(_ => Sse(body));
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        Assert.Contains("cek_status", ex.Message);
        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public async Task Delta_content_that_changed_shape_is_a_clear_failure_not_empty_text()
    {
        var body = new PushBody();
        body.Write("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}}]}\n\n");
        body.Write(Done);
        body.End();

        var (client, _) = NewClient(_ => Sse(body));
        var ex = await Assert.ThrowsAsync<AiStreamException>(() => client.CompleteStreamingAsync(Request, (_, _) => Task.CompletedTask,
            CancellationToken.None));

        Assert.Contains("bukan teks", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "model_not_allowed", true)]
    [InlineData(HttpStatusCode.NotFound, "model_not_found", true)]
    [InlineData(HttpStatusCode.BadRequest, "budget_exceeded", false)]
    public async Task Status_errors_before_the_body_keep_the_nonstream_contract(
        HttpStatusCode status, string code, bool unavailable)
    {
        var deltas = new List<string>();
        var (client, handler) = NewClient(_ => Json(status, new JsonObject
            { ["error"] = new JsonObject { ["code"] = code } }.ToJsonString()));

        var ex = await Assert.ThrowsAnyAsync<AiException>(() => client.CompleteStreamingAsync(Request, (delta, _) =>
        {
            deltas.Add(delta);
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Equal(unavailable, ex is AiModelUnavailableException);
        Assert.IsNotType<AiStreamException>(ex); // belum ada body/usage: bukan kegagalan stream
        Assert.Empty(deltas);
        Assert.Equal(1, handler.Calls);
    }
}
