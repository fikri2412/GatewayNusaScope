using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Contracts;
using Polly;

namespace OperatorAi.Shared.Ai;

/// <summary>
/// OpenAI Chat Completions ke gateway (<c>Ai:BaseUrl</c> + <c>Ai:MessagesPath</c>), auth <c>Bearer Ai:ApiKey</c> (key gateway,
/// bukan key provider). Retry 429/5xx/timeout dikerjakan handler resilience (<see cref="AiServiceCollectionExtensions"/>);
/// jalur stream memakai header yang sama, hanya body-nya <c>stream=true</c> dan batas body dipasang sendiri.
/// Batas waktu/byte satu panggilan datang dari <see cref="AiRequest.Limits"/> (snapshot pekerjaan); opsi <c>Ai:</c> proses
/// hanya dipakai bila request tidak membawa snapshot.
/// Tidak pernah mencatat API key, isi prompt, reasoning, atau argumen tool.
/// </summary>
public sealed class OpenAiCompatibleClient(HttpClient http, IOptions<AiOptions> options, ILogger<OpenAiCompatibleClient> logger)
    : IAiClient
{
    private const int MaxLoggedBodyChars = 2000;
    private static readonly ProductInfoHeaderValue ClientAgent = new("OperatorAi", "1.0");

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        Validate(request);
        var limits = EffectiveLimits(request);
        using var outbound = CreateRequest(request, options.Value, limits, stream: false);
        // Batas total snapshot mengikat seluruh panggilan, termasuk pembacaan body: timeout handler resilience berhenti
        // saat header tiba (body dibuffer HttpClient setelah pipeline selesai), jadi body yang menggantung hanya bisa
        // dibatalkan dari sini. Batas total handler tetap berlaku untuk fase header beserta retry-nya.
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TimeSpan.FromSeconds(limits.TotalRequestTimeoutSeconds));
        try
        {
            using var res = await SendAsync(outbound, HttpCompletionOption.ResponseContentRead, total.Token);
            var text = await res.Content.ReadAsStringAsync(total.Token);
            ThrowIfError(res, text, request.Model);
            return Parse(text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && total.IsCancellationRequested)
        {
            throw new AiException($"AI tidak mengirim jawaban lengkap dalam {limits.TotalRequestTimeoutSeconds} detik.");
        }
    }

    public async Task<AiResponse> CompleteStreamingAsync(AiRequest request, Func<string, CancellationToken, Task> onTextDelta,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onTextDelta);
        Validate(request);
        var limits = EffectiveLimits(request);
        using var outbound = CreateRequest(request, options.Value, limits, stream: true);
        // ResponseHeadersRead: handler resilience hanya membatasi sampai header; sisa body dibatasi ReadStreamAsync.
        // Header baru dikirim gateway setelah event pertama upstream terbaca, jadi menunggu header = menunggu byte pertama.
        using var firstByte = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstByte.CancelAfter(TimeSpan.FromSeconds(limits.StreamFirstByteSeconds));
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, firstByte.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && firstByte.IsCancellationRequested)
        {
            throw new AiFirstByteTimeoutException(limits.StreamFirstByteSeconds);
        }
        firstByte.CancelAfter(Timeout.InfiniteTimeSpan); // header sudah tiba: batas ini tidak lagi berlaku
        using var res = response;
        if (!res.IsSuccessStatusCode)
            ThrowIfError(res, await res.Content.ReadAsStringAsync(ct), request.Model);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await ReadStreamAsync(stream, onTextDelta, limits, ct);
    }

    /// <summary>
    /// Batas satu panggilan: snapshot pekerjaan bila ada, opsi proses untuk pemakaian langsung tanpa snapshot.
    /// </summary>
    private AiRuntimeLimits EffectiveLimits(AiRequest request) => request.Limits ?? new AiRuntimeLimits
    {
        AttemptTimeoutSeconds = options.Value.AttemptTimeoutSeconds,
        TotalRequestTimeoutSeconds = options.Value.TotalRequestTimeoutSeconds,
        StreamFirstByteSeconds = options.Value.StreamFirstByteSeconds,
        StreamIdleSeconds = options.Value.StreamIdleSeconds,
        MaxStreamSeconds = options.Value.MaxStreamSeconds,
        MaxStreamBytes = options.Value.MaxStreamBytes,
        MaxStreamEventBytes = options.Value.MaxStreamEventBytes,
    };

    private static void Validate(AiRequest request)
    {
        if (request.MaxTokens is < 1 or > 64000)
            throw new ArgumentOutOfRangeException(nameof(request.MaxTokens));
        ModelSnapshot.Validate(request.Model);
        if (request.ConversationId == Guid.Empty)
            throw new ArgumentException("ConversationId wajib diisi untuk session AI.", nameof(request));
    }

    /// <summary>Request kedua jalur identik; hanya bendera stream yang membedakan.</summary>
    private static HttpRequestMessage CreateRequest(AiRequest request, AiOptions o, AiRuntimeLimits limits, bool stream)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
        foreach (var m in request.Messages) messages.Add(m.ToJson());
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages };
        // Tanpa anggaran dari chatbot, field tidak dikirim: batas keluaran sepenuhnya urusan gateway (policy + model).
        if (request.MaxTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort)) body["reasoning_effort"] = request.ReasoningEffort;
        if (request.Tools is { Count: > 0 }) body["tools"] = request.Tools.DeepClone();
        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        var outbound = new HttpRequestMessage(HttpMethod.Post, o.MessagesPath) { Content = JsonContent.Create(body) };
        // Generator timeout handler resilience membaca batas panggilan ini dari sini; tidak pernah ikut ke gateway.
        outbound.Options.Set(AiRequestOptions.Limits, limits);
        outbound.Headers.UserAgent.Add(ClientAgent);
        outbound.Headers.Add("x-opencode-session", request.ConversationId.ToString("D"));
        if (!string.IsNullOrWhiteSpace(request.Tags))
            outbound.Headers.Add("X-Gateway-Tags", request.Tags);
        return outbound;
    }

    /// <summary>HttpRequestException, timeout resilience, sirkuit terbuka, dsb. menjadi satu jenis error.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage outbound, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(outbound, completion, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"AI tidak terjangkau: {ex.Message}", ex);
        }
    }

    /// <summary>Status bukan 2xx; pencabutan model tetap terminal, pesannya sama untuk jalur utuh dan stream.</summary>
    private void ThrowIfError(HttpResponseMessage res, string text, string model)
    {
        if (res.IsSuccessStatusCode) return;
        var shown = text.Length > MaxLoggedBodyChars ? text[..MaxLoggedBodyChars] : text;
        logger.LogError("AI menjawab {Status}: {Body}", (int)res.StatusCode, shown);
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            var code = ReadErrorCode(text);
            if (res.StatusCode == HttpStatusCode.Forbidden && code == "model_not_allowed"
                || res.StatusCode == HttpStatusCode.NotFound && code == "model_not_found")
                throw new AiModelUnavailableException(model);
        }
        throw new AiException($"AI menjawab {(int)res.StatusCode}: {shown}");
    }

    /// <summary>
    /// Baca body SSE sampai <c>[DONE]</c>. Teks publik diteruskan segera (stream masih terbuka), sementara
    /// reasoning dan fragmen tool call hanya diakumulasi supaya tidak pernah sampai ke pemanggil.
    /// </summary>
    private async Task<AiResponse> ReadStreamAsync(Stream stream, Func<string, CancellationToken, Task> onTextDelta,
        AiRuntimeLimits limits, CancellationToken ct)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var tools = new SortedDictionary<int, ToolCallFragment>();
        var payload = new StringBuilder();
        var usage = (Prompt: 0, Completion: 0, Available: false);
        string? eventName = null, finish = null;
        var done = false;

        // Batas body: diam antar baca dan durasi total; batas byte ditangani SseLineReader.
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(limits.MaxStreamSeconds));
        var idleSpan = TimeSpan.FromSeconds(limits.StreamIdleSeconds);
        var reader = new SseLineReader(stream, limits.MaxStreamBytes, limits.MaxStreamEventBytes);

        AiStreamException Fail(string message, Exception? inner = null)
        {
            // Log hanya ringkasan; isi jawaban/reasoning/argumen tidak pernah ikut. Batal dari pemanggil bukan error.
            if (!ct.IsCancellationRequested) logger.LogError("Stream AI gagal: {Reason}", message);
            return new AiStreamException(message, usage.Prompt, usage.Completion, usage.Available, ct.IsCancellationRequested, inner);
        }

        // Fragmen tool call digabung per index (index memang dikirim OpenAI maupun adaptor gateway); tanpa index,
        // fragmen ber-id/nama membuka panggilan baru dan sisanya dianggap lanjutan yang terakhir.
        void Accumulate(JsonObject fragment)
        {
            var id = Str(fragment["id"]);
            var name = Str(fragment["function"]?["name"]);
            var index = fragment["index"] is JsonValue value && value.TryGetValue<int>(out var parsed)
                ? parsed
                : id is not null || name is not null ? tools.Count : tools.Count - 1;
            if (index < 0) index = tools.Count;
            if (!tools.TryGetValue(index, out var call)) tools[index] = call = new ToolCallFragment(index);
            if (id is not null) call.Id = id;
            if (name is not null) call.Name += name;
            if (Str(fragment["function"]?["arguments"]) is { } arguments) call.Arguments.Append(arguments);
        }

        // Event lengkap (dibatasi baris kosong): kembalikan teks delta publik yang siap diteruskan, atau null.
        string? Dispatch()
        {
            if (payload.Length == 0)
            {
                eventName = null;
                return null;
            }
            var raw = payload.ToString();
            payload.Clear();
            var name = eventName;
            eventName = null;
            if (raw.Trim() == "[DONE]")
            {
                done = true;
                return null;
            }

            JsonObject chunk;
            try
            {
                chunk = JsonNode.Parse(raw) as JsonObject ?? throw new FormatException("payload bukan objek JSON.");
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                throw Fail("Payload SSE bukan JSON objek.", ex);
            }

            // Bentuk chunk yang menyimpang diperlakukan sama seperti kegagalan stream lain: usage yang sudah terlihat tetap dibawa.
            try
            {
                if (chunk["error"] is JsonObject error) throw Fail(DescribeError(Str(error["code"])));
                if (name == "error") throw Fail(DescribeError(Str(chunk["code"])));

                if (chunk["usage"] is { } reported)
                {
                    try
                    {
                        usage = ReadUsage(reported);
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException or OverflowException)
                    {
                        throw Fail("Usage stream tidak dikenali.", ex); // usage sah sebelumnya tetap dipertahankan
                    }
                }

                if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) return null;
                if (choice["finish_reason"] is JsonValue reason && reason.TryGetValue<string>(out var text)) finish = text;
                if (choice["delta"] is not JsonObject delta) return null;

                var publicText = Str(delta["content"]);
                if (delta["content"] is not null && publicText is null) throw Fail("Delta content stream bukan teks.");
                if (publicText is { Length: > 0 }) content.Append(publicText);
                if (Str(delta["reasoning_content"]) is { Length: > 0 } privateText) reasoning.Append(privateText);
                if (delta["tool_calls"] is JsonArray fragments)
                    foreach (var node in fragments)
                        if (node is JsonObject fragment) Accumulate(fragment);
                return publicText is { Length: > 0 } ? publicText : null;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
            {
                throw Fail("Chunk SSE tidak sesuai kontrak.", ex);
            }
        }

        // Satu CTS untuk seluruh stream: timer dipasang hanya selama baca yang bisa memblokir, jadi tidak ada
        // alokasi CTS per baris dan waktu dispatch/callback tidak pernah dihitung sebagai diam.
        using var gap = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);

        while (!done)
        {
            string? line;
            try
            {
                gap.CancelAfter(idleSpan);
                line = await reader.ReadLineAsync(gap.Token);
                gap.CancelAfter(Timeout.InfiniteTimeSpan); // batas diam hanya berlaku selama baca
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Fail(overall.IsCancellationRequested
                    ? $"Stream AI melewati batas durasi {limits.MaxStreamSeconds} dtk."
                    : $"Stream AI diam lebih lama dari {limits.StreamIdleSeconds} dtk.");
            }
            catch (OperationCanceledException ex)
            {
                throw Fail("Stream AI dibatalkan.", ex);
            }
            catch (StreamFramingException ex)
            {
                throw Fail(ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
            {
                throw Fail($"Stream AI terputus: {Brief(ex.Message)}", ex);
            }

            if (line is null)
            {
                // Event terakhir tanpa baris kosong tetap diproses; kelengkapannya dinilai setelah loop.
                var tail = Dispatch();
                if (tail is not null) await onTextDelta(tail, ct);
                break;
            }
            if (line.Length == 0)
            {
                var delta = Dispatch();
                if (delta is not null) await onTextDelta(delta, ct);
                continue;
            }
            if (line[0] == ':') continue; // komentar/heartbeat
            if (TryField(line, "event", out var name))
            {
                eventName = name;
                continue;
            }
            if (TryField(line, "data", out var data))
            {
                if (payload.Length > 0) payload.Append('\n');
                payload.Append(data);
            }
            // id:/retry:/baris tanpa kolon tidak dipakai.
        }

        // Jawaban hanya utuh bila upstream menutup stream secara normal; potongan tanpa penutup bukan sukses.
        if (!done) throw Fail("Stream AI berakhir tanpa [DONE].");
        if (finish is null) throw Fail("Stream AI tanpa finish_reason.");
        foreach (var call in tools.Values)
        {
            if (string.IsNullOrEmpty(call.Id)) throw Fail("Stream AI mengirim tool call tanpa id.");
            if (call.Name.Length == 0) throw Fail("Stream AI mengirim tool call tanpa nama.");
            var arguments = call.Arguments.ToString();
            if (arguments.Trim().Length > 0 && !IsJson(arguments)) throw Fail($"Argumen tool '{call.Name}' bukan JSON yang valid.");
        }
        if (finish == "tool_calls" && tools.Count == 0) throw Fail("Stream AI menyatakan tool_calls tanpa tool call.");

        var calls = tools.Count == 0 ? null : new JsonArray(tools.Values.Select(ToolCallJson).ToArray());
        var message = new AiMessage("assistant", content.ToString(), calls, null,
            reasoning.Length == 0 ? null : reasoning.ToString());
        return new AiResponse(message.Content ?? "", finish, usage.Prompt, usage.Completion, message, usage.Available);
    }

    private static bool TryField(string line, string field, out string value)
    {
        value = "";
        if (line.Length <= field.Length || !line.StartsWith(field, StringComparison.Ordinal) || line[field.Length] != ':')
            return false;
        var rest = line[(field.Length + 1)..];
        value = rest.Length > 0 && rest[0] == ' ' ? rest[1..] : rest;
        return true;
    }

    /// <summary>Bentuk tool call yang sama dengan pesan mentah jalur utuh supaya bisa dikirim ulang apa adanya.</summary>
    private static JsonObject ToolCallJson(ToolCallFragment call) => new()
    {
        ["index"] = call.Index,
        ["id"] = call.Id ?? "",
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToString() },
    };

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Ringkasan error SSE: hanya kode aman (dipotong), tanpa isi mentah atau pesan upstream.</summary>
    private static string DescribeError(string? code) =>
        string.IsNullOrEmpty(code) ? "Stream AI berhenti." : $"Stream AI berhenti ({Brief(code)}).";

    /// <summary>Ringkasan pendek untuk pesan/log; tidak pernah memuat isi jawaban, prompt, atau argumen tool.</summary>
    private static string Brief(string text) => text.Length > 200 ? text[..200] : text;

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? ReadErrorCode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
                return code.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    internal static AiResponse Parse(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)!;
            var choice = root["choices"]![0]!;
            var rawMessage = choice["message"]!.AsObject();
            rawMessage["role"] = "assistant"; // jawaban selalu dari assistant; provider yang menghilangkan role tidak boleh membuat parsing gagal
            var message = AiMessage.FromJson(rawMessage);
            var finish = choice["finish_reason"] is JsonValue f && f.TryGetValue<string>(out var reason) ? reason : null;
            var (promptTokens, completionTokens, usageAvailable) = ReadUsage(root["usage"]);
            return new AiResponse(message.Content ?? "", finish, promptTokens, completionTokens, message, usageAvailable);
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException
            or ArgumentOutOfRangeException or FormatException or OverflowException)
        {
            throw new AiException($"Respons AI tidak dikenali: {ex.Message}", ex);
        }
    }

    /// <summary>Usage boleh absen (unknown) atau sebagian; angka negatif bukan klaim token yang sah.</summary>
    private static (int Prompt, int Completion, bool Available) ReadUsage(JsonNode? usage)
    {
        // usage bisa hilang seluruhnya (provider/gateway tanpa include_usage) atau hanya sebagian.
        var prompt = usage?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var completion = usage?["completion_tokens"]?.GetValue<int>() ?? 0;
        if (prompt < 0 || completion < 0) throw new FormatException("Usage token negatif.");
        return (prompt, completion, usage?["prompt_tokens"] is not null && usage?["completion_tokens"] is not null);
    }

    /// <summary>Akumulator fragmen tool call per index; argumen baru dipercaya setelah stream lengkap divalidasi.</summary>
    private sealed class ToolCallFragment(int index)
    {
        public int Index { get; } = index;
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>Framing SSE rusak (batas byte atau UTF-8 tidak sah); pemanggil mengubahnya jadi <see cref="AiStreamException"/>.</summary>
    private sealed class StreamFramingException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// Framing baris SSE berbatas: tiap baris di-decode UTF-8 secara ketat (CR/LF ASCII tidak pernah memotong
    /// karakter multibyte) dan satu event — termasuk baris komentar/event sampai baris kosong penutupnya —
    /// dibatasi byte-nya supaya stream liar tidak menghabiskan memori.
    /// </summary>
    private sealed class SseLineReader(Stream stream, int maxTotalBytes, int maxEventBytes)
    {
        private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private readonly byte[] _buffer = new byte[16 * 1024];
        private byte[] _line = new byte[1024];
        private int _start, _end, _length, _eventBytes;
        private long _total;
        private bool _first = true, _skipLf;

        /// <returns>Baris tanpa akhiran baris; null di akhir stream.</returns>
        public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_skipLf && _start < _end)
                {
                    if (_buffer[_start] == (byte)'\n') _start++;
                    _skipLf = false;
                }
                for (var i = _start; i < _end; i++)
                {
                    var b = _buffer[i];
                    if (b is not ((byte)'\n' or (byte)'\r')) continue;
                    Append(_start, i - _start);
                    _start = i + 1;
                    _skipLf = b == (byte)'\r';
                    return Take();
                }
                Append(_start, _end - _start);
                _start = _end = 0;
                var read = await stream.ReadAsync(_buffer.AsMemory(), ct);
                if (read == 0) return _length == 0 ? null : Take();
                _total += read;
                if (_total > maxTotalBytes) throw new StreamFramingException($"Body stream melebihi {maxTotalBytes} byte.");
                _end = read;
            }
        }

        private void Append(int offset, int count)
        {
            if (_length + count > maxEventBytes) throw new StreamFramingException($"Baris SSE melebihi {maxEventBytes} byte.");
            if (_length + count > _line.Length)
                Array.Resize(ref _line, Math.Min(maxEventBytes, Math.Max(_line.Length * 2, _length + count)));
            Buffer.BlockCopy(_buffer, offset, _line, _length, count);
            _length += count;
        }

        private string Take()
        {
            string text;
            try
            {
                text = Strict.GetString(_line, 0, _length);
            }
            catch (DecoderFallbackException ex)
            {
                throw new StreamFramingException("Stream AI berisi byte UTF-8 yang tidak sah.", ex);
            }
            if (_first)
            {
                _first = false;
                if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            }
            // Baris kosong menutup event; batas satu event termasuk baris data/komentar di dalamnya (+1 akhiran).
            _eventBytes += _length + 1;
            if (_length == 0) _eventBytes = 0;
            else if (_eventBytes > maxEventBytes) throw new StreamFramingException($"Event SSE melebihi {maxEventBytes} byte.");
            _length = 0;
            return text;
        }
    }
}

/// <summary>
/// Kunci opsi <see cref="HttpRequestMessage"/> berisi <see cref="AiRuntimeLimits"/> panggilan ini. Klien memasangnya, dan
/// generator timeout handler resilience membacanya lewat <c>ResilienceContext</c>; nilainya tidak pernah terkirim ke gateway.
/// </summary>
internal static class AiRequestOptions
{
    public static readonly HttpRequestOptionsKey<AiRuntimeLimits> Limits = new("OperatorAi.AiRuntimeLimits");
}

public static class AiServiceCollectionExtensions
{
    /// <summary>Worker memvalidasi key untuk inferensi; API memvalidasinya di <c>AddGatewaySettings</c> untuk polling G6.</summary>
    public static IServiceCollection AddAiClient(this IServiceCollection services)
    {
        services.AddOptions<AiOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Ai:ApiKey wajib diisi (key gateway gw_..., lewat user-secrets).")
            .ValidateOnStart();

        services.AddHttpClient<IAiClient, OpenAiCompatibleClient>((sp, c) =>
            {
                var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
                c.BaseAddress = new Uri(o.BaseUrl);
                c.DefaultRequestHeaders.Authorization = new("Bearer", o.ApiKey);
                c.Timeout = Timeout.InfiniteTimeSpan; // batas total dipasang handler resilience
            })
            .AddStandardResilienceHandler(r =>
            {
                // Generator memakai budget snapshot per request; nilai statis dan jendela breaker tetap baseline lama.
                // Validator resilience membandingkan SamplingDuration dengan Timeout statis, bukan hasil generator.
                r.AttemptTimeout.Timeout = TimeSpan.FromSeconds(AiRuntimeLimits.Default.AttemptTimeoutSeconds);
                r.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(AiRuntimeLimits.Default.TotalRequestTimeoutSeconds);
                r.AttemptTimeout.TimeoutGenerator = args => new ValueTask<TimeSpan>(TimeSpan.FromSeconds(
                    LimitsOf(args.Context)?.AttemptTimeoutSeconds ?? AiRuntimeLimits.Default.AttemptTimeoutSeconds));
                r.TotalRequestTimeout.TimeoutGenerator = args => new ValueTask<TimeSpan>(TimeSpan.FromSeconds(
                    LimitsOf(args.Context)?.TotalRequestTimeoutSeconds ?? AiRuntimeLimits.Default.TotalRequestTimeoutSeconds));
                r.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(2 * AiRuntimeLimits.Default.AttemptTimeoutSeconds);
            });

        // Fallback mengambil halaman langsung dari internet: handler anti-SSRF (resolve + validasi alamat publik tepat
        // sebelum socket dibuka, tanpa redirect/proxy lingkungan) plus batas waktu dan ukuran respons.
        services.AddHttpClient<FallbackWebSearch>(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(15);
                c.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(PublicUrlGuard.CreateHandler);
        services.AddHttpClient<IWebSearch, GatewayWebSearch>((sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            c.BaseAddress = new Uri(o.BaseUrl);
            c.DefaultRequestHeaders.Authorization = new("Bearer", o.ApiKey);
            c.Timeout = TimeSpan.FromSeconds(o.TotalRequestTimeoutSeconds);
        }).RemoveAllLoggers();
        return services;
    }

    /// <summary>Batas yang dipasang klien pada request yang sedang dikirim; null bila request tidak dibuat klien ini.</summary>
    private static AiRuntimeLimits? LimitsOf(ResilienceContext context)
    {
#pragma warning disable EXTEXP0001 // GetRequestMessage(): API experimental, satu-satunya cara resmi membaca request dari generator
        var request = context.GetRequestMessage();
#pragma warning restore EXTEXP0001
        return request is not null && request.Options.TryGetValue(AiRequestOptions.Limits, out var limits) ? limits : null;
    }
}
