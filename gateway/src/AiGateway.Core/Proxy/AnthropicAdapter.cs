using System.Text;
using System.Text.Json.Nodes;
using static AiGateway.Core.Proxy.JsonAccess;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Terjemahan Chat Completions (format klien) ke Anthropic Messages (format upstream) dan sebaliknya, untuk route
/// ber-family <c>anthropic_messages</c>.
/// Didukung: system/developer, teks, gambar (data URL base64 png/jpeg/gif/webp atau https), tool_calls dan hasil tool,
/// tools, tool_choice, max_tokens, temperature, top_p, stop, user. Konten yang tidak punya padanan ditolak 400, bukan
/// dibuang diam-diam; field tuning lain (n, penalti, response_format, ...) diabaikan.
/// </summary>
internal sealed class AnthropicAdapter : IFamilyAdapter
{
    public static readonly AnthropicAdapter Instance = new();

    /// <summary>Anthropic mewajibkan <c>max_tokens</c>; dipakai bila klien dan model tidak menentukannya.</summary>
    public const int DefaultMaxTokens = 4096;

    public const string Version = "2023-06-01";

    /// <summary>
    /// <c>…/openai/v1/</c> menjadi <c>…/anthropic/v1/messages</c> (jalur OpenCode per family); base URL tanpa
    /// segmen <c>/openai/</c> dipakai apa adanya.
    /// </summary>
    public Uri RequestUri(Uri baseUri)
    {
        var path = baseUri.AbsolutePath.Replace("/openai/", "/anthropic/", StringComparison.Ordinal).TrimEnd('/');
        return new UriBuilder(baseUri) { Path = path + "/messages", Query = "" }.Uri;
    }

    public void AddHeaders(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("anthropic-version", Version);

    public ChatChunkReader TranslateStream(TextReader upstream) => new AnthropicStreamReader(upstream);

    public bool TryToRequest(
        JsonObject chat, string upstreamModel, bool stream, int? modelMaxOutput, out JsonObject request, out string error)
    {
        request = new JsonObject();
        error = "";
        var messages = new JsonArray();
        var system = new List<string>();
        JsonObject? current = null;

        // Anthropic menolak giliran berurutan dengan role sama; hasil tool dan pesan user sesudahnya digabung.
        void Add(string role, JsonNode block)
        {
            if (current is not null && Str(current["role"]) == role) ((JsonArray)current["content"]!).Add(block);
            else messages.Add(current = new JsonObject { ["role"] = role, ["content"] = new JsonArray { block } });
        }

        foreach (var node in Get(chat, "messages") as JsonArray ?? [])
        {
            if (node is not JsonObject message) { error = "Each message must be an object."; return false; }
            switch (Str(message["role"]))
            {
                case "system" or "developer":
                    if (!TryTextOf(message["content"], out var systemText, out error)) return false;
                    if (systemText.Length > 0) system.Add(systemText);
                    break;
                case "user":
                    if (!TryBlocks(message["content"], out var userBlocks, out error)) return false;
                    foreach (var block in userBlocks) Add("user", block);
                    break;
                case "assistant":
                    if (!TryBlocks(message["content"], out var assistantBlocks, out error)) return false;
                    foreach (var block in assistantBlocks) Add("assistant", block);
                    foreach (var call in message["tool_calls"] as JsonArray ?? [])
                    {
                        var fn = Get(call, "function");
                        Add("assistant", new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = Str(Get(call, "id")) ?? "",
                            ["name"] = Str(Get(fn, "name")) ?? "",
                            ["input"] = ParseObject(Str(Get(fn, "arguments"))) ?? new JsonObject(),
                        });
                    }
                    break;
                case "tool":
                    if (!TryTextOf(message["content"], out var toolText, out error)) return false;
                    Add("user", new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = Str(message["tool_call_id"]) ?? "",
                        ["content"] = toolText,
                    });
                    break;
                default:
                    error = $"Unsupported message role '{Str(message["role"])}'.";
                    return false;
            }
        }

        request["model"] = upstreamModel;
        request["max_tokens"] = LongOf(chat["max_tokens"]) ?? LongOf(chat["max_completion_tokens"]) ?? modelMaxOutput ?? DefaultMaxTokens;
        request["messages"] = messages;
        if (system.Count > 0) request["system"] = string.Join("\n\n", system);
        if (stream) request["stream"] = true;
        if (chat["temperature"] is JsonValue t && t.TryGetValue<double>(out var temperature))
            request["temperature"] = Math.Min(temperature, 1.0); // OpenAI 0..2, Anthropic 0..1
        if (chat["top_p"] is JsonValue p && p.TryGetValue<double>(out var topP)) request["top_p"] = topP;
        if (Str(chat["stop"]) is { } oneStop) request["stop_sequences"] = new JsonArray(oneStop);
        else if (chat["stop"] is JsonArray stops) request["stop_sequences"] = stops.DeepClone();
        if (Str(chat["user"]) is { } user) request["metadata"] = new JsonObject { ["user_id"] = user };

        return TryAddTools(chat, request, out error);
    }

    private static bool TryAddTools(JsonObject chat, JsonObject request, out string error)
    {
        error = "";
        if (chat["tools"] is not JsonArray { Count: > 0 } tools) return true;
        var choice = chat["tool_choice"];
        if (Str(choice) == "none") return true; // tanpa tools sama dengan melarang pemanggilan

        var mapped = new JsonArray();
        foreach (var tool in tools)
        {
            var fn = Get(tool, "function") as JsonObject;
            if (Str(Get(tool, "type")) != "function" || Str(Get(fn, "name")) is not { Length: > 0 } name)
            {
                error = "Only function tools are supported.";
                return false;
            }
            var definition = new JsonObject { ["name"] = name };
            if (Str(Get(fn, "description")) is { } description) definition["description"] = description;
            definition["input_schema"] = Get(fn, "parameters")?.DeepClone()
                ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            mapped.Add(definition);
        }
        request["tools"] = mapped;

        if (Str(choice) == "required") request["tool_choice"] = new JsonObject { ["type"] = "any" };
        else if (Str(Get(Get(choice, "function"), "name")) is { } forced)
            request["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = forced };
        return true;
    }

    private static bool TryBlocks(JsonNode? content, out List<JsonNode> blocks, out string error)
    {
        blocks = [];
        error = "";
        if (content is null) return true;
        if (Str(content) is { } plain)
        {
            if (plain.Length > 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = plain });
            return true;
        }
        if (content is not JsonArray parts) { error = "Message content must be a string or an array of parts."; return false; }

        foreach (var part in parts)
        {
            switch (Str(Get(part, "type")))
            {
                case "text":
                    if (Str(Get(part, "text")) is { Length: > 0 } text) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    break;
                case "image_url":
                    if (ImageSource(Str(Get(Get(part, "image_url"), "url"))) is not { } source)
                    {
                        error = "image_url must be a base64 data URL (image/png, image/jpeg, image/gif, or image/webp) "
                            + "or an https URL; other image sources are not fetched by the gateway.";
                        return false;
                    }
                    blocks.Add(new JsonObject { ["type"] = "image", ["source"] = source });
                    break;
                default:
                    error = $"Unsupported content part '{Str(Get(part, "type"))}'.";
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <c>data:…;base64,…</c> dengan jenis gambar yang diterima Anthropic (png/jpeg/gif/webp), atau URL https yang
    /// diteruskan apa adanya; selain itu null (ditolak pemanggil: gateway tidak pernah mengambil gambar sendiri).
    /// </summary>
    private static JsonObject? ImageSource(string? url)
    {
        if (url is null) return null;
        if (url.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = url.IndexOf(',');
            var header = comma > 5 ? url[5..comma] : "";
            if (!header.EndsWith(";base64", StringComparison.Ordinal)) return null;
            var mediaType = header[..^7];
            return ImageMediaTypes.Contains(mediaType)
                ? new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = url[(comma + 1)..] }
                : null;
        }
        return url.StartsWith("https://", StringComparison.Ordinal) ? new JsonObject { ["type"] = "url", ["url"] = url } : null;
    }

    /// <summary>Jenis gambar inline yang diteruskan ke Anthropic; selain ini ditolak klien, bukan oleh provider.</summary>
    private static readonly HashSet<string> ImageMediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public JsonObject? ToChatCompletion(JsonObject message)
    {
        if (message["content"] is not JsonArray blocks) return null;
        var text = new StringBuilder();
        var toolCalls = new JsonArray();
        foreach (var block in blocks)
        {
            switch (Str(Get(block, "type")))
            {
                case "text": text.Append(Str(Get(block, "text"))); break;
                case "tool_use":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = Str(Get(block, "id")),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = Str(Get(block, "name")),
                            ["arguments"] = (Get(block, "input") ?? new JsonObject()).ToJsonString(),
                        },
                    });
                    break;
            } // thinking dan blok lain tidak punya padanan di Chat Completions
        }

        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = toolCalls.Count > 0 && text.Length == 0 ? null : text.ToString() };
        if (toolCalls.Count > 0) assistant["tool_calls"] = toolCalls;

        var result = new JsonObject
        {
            ["id"] = Str(message["id"]),
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = Str(message["model"]),
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = assistant,
                ["finish_reason"] = FinishReason(Str(message["stop_reason"])),
            }),
        };
        // Usage upstream tanpa satu pun counter yang dikenali tidak dikirim sebagai objek kosong berisi nol palsu.
        if (message["usage"] is JsonObject usage && UsageOf(usage) is { Count: > 0 } normalized) result["usage"] = normalized;
        return result;
    }

    /// <summary>
    /// Anthropic memisahkan token ter-cache dari <c>input_tokens</c>; OpenAI memasukkannya ke <c>prompt_tokens</c> dan
    /// menyebut bagian ter-cache di <c>prompt_tokens_details</c> (itu yang dibaca penagihan). Komponen yang tidak
    /// dilaporkan upstream tidak ditebak sebagai nol: field yang tidak diketahui dihilangkan, sehingga pemanggil bisa
    /// menandai baris sebagai batas bawah, bukan pengukuran penuh.
    /// </summary>
    public static JsonObject UsageOf(JsonObject usage)
    {
        var input = TokenCount(usage["input_tokens"]);
        var cached = TokenCount(usage["cache_read_input_tokens"]);
        var created = TokenCount(usage["cache_creation_input_tokens"]);
        var completion = TokenCount(usage["output_tokens"]);
        long? prompt = input is null ? null : (long)input.Value + (cached ?? 0) + (created ?? 0);
        if (prompt > int.MaxValue) prompt = null;

        var result = new JsonObject();
        if (prompt is not null) result["prompt_tokens"] = prompt.Value;
        if (completion is not null) result["completion_tokens"] = completion.Value;
        if (prompt is not null && completion is not null) result["total_tokens"] = prompt.Value + completion.Value;
        if (cached is not null) result["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached.Value };
        return result;
    }

    public static string? FinishReason(string? stopReason) => stopReason switch
    {
        null => null,
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "refusal" => "content_filter",
        _ => "stop", // end_turn, stop_sequence, pause_turn
    };
}
