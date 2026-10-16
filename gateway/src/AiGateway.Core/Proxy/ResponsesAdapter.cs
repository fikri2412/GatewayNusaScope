using System.Text;
using System.Text.Json.Nodes;
using static AiGateway.Core.Proxy.JsonAccess;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Terjemahan Chat Completions (format klien) ke OpenAI Responses (format upstream) dan sebaliknya, untuk route
/// ber-family <c>openai_responses</c>. Tanpa state di upstream: <c>store:false</c> dan seluruh percakapan dikirim
/// ulang di <c>input</c> tiap permintaan.
/// Didukung: system/developer (menjadi <c>instructions</c>), teks, gambar (data URL base64 png/jpeg/gif/webp atau
/// https), tool_calls dan hasil tool, tools (function; <c>strict</c> default false seperti Chat Completions),
/// tool_choice, max_tokens, temperature, top_p, user. Konten tanpa padanan ditolak 400; field lain (n, stop, penalti,
/// response_format, ...) diabaikan. Item reasoning dari upstream tidak diteruskan.
/// </summary>
internal sealed class ResponsesAdapter : IFamilyAdapter
{
    public static readonly ResponsesAdapter Instance = new();

    /// <summary><c>…/openai/v1/</c> menjadi <c>…/openai/v1/responses</c>.</summary>
    public Uri RequestUri(Uri baseUri) =>
        new UriBuilder(baseUri) { Path = baseUri.AbsolutePath.TrimEnd('/') + "/responses", Query = "" }.Uri;

    public void AddHeaders(HttpRequestMessage request) { }

    public ChatChunkReader TranslateStream(TextReader upstream) => new ResponsesStreamReader(upstream);

    public bool TryToRequest(
        JsonObject chat, string upstreamModel, bool stream, int? modelMaxOutput, out JsonObject request, out string error)
    {
        request = new JsonObject();
        error = "";
        var input = new JsonArray();
        var instructions = new List<string>();

        foreach (var node in Get(chat, "messages") as JsonArray ?? [])
        {
            if (node is not JsonObject message) { error = "Each message must be an object."; return false; }
            switch (Str(message["role"]))
            {
                case "system" or "developer":
                    if (!TryTextOf(message["content"], out var instruction, out error)) return false;
                    if (instruction.Length > 0) instructions.Add(instruction);
                    break;
                case "user":
                    if (!TryUserContent(message["content"], out var content, out error)) return false;
                    if (content is not null) input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                case "assistant":
                    // Riwayat assistant dikirim sebagai bagian output_text (bentuk yang dikembalikan Responses);
                    // teks tidak digabung menjadi satu string supaya batas bagian tetap utuh.
                    if (!TryAssistantContent(message["content"], out var said, out error)) return false;
                    if (said is not null) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = said });
                    foreach (var call in message["tool_calls"] as JsonArray ?? [])
                    {
                        var fn = Get(call, "function");
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = Str(Get(call, "id")) ?? "",
                            ["name"] = Str(Get(fn, "name")) ?? "",
                            ["arguments"] = Str(Get(fn, "arguments")) ?? "{}",
                        });
                    }
                    break;
                case "tool":
                    if (!TryTextOf(message["content"], out var output, out error)) return false;
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = Str(message["tool_call_id"]) ?? "",
                        ["output"] = output,
                    });
                    break;
                default:
                    error = $"Unsupported message role '{Str(message["role"])}'.";
                    return false;
            }
        }

        request["model"] = upstreamModel;
        request["input"] = input;
        request["store"] = false;
        if (instructions.Count > 0) request["instructions"] = string.Join("\n\n", instructions);
        if (stream) request["stream"] = true;
        if ((LongOf(chat["max_tokens"]) ?? LongOf(chat["max_completion_tokens"])) is { } max) request["max_output_tokens"] = max;
        if (chat["temperature"] is JsonValue t && t.TryGetValue<double>(out var temperature)) request["temperature"] = temperature;
        if (chat["top_p"] is JsonValue p && p.TryGetValue<double>(out var topP)) request["top_p"] = topP;
        if (Str(chat["user"]) is { } user) request["user"] = user;

        return TryAddTools(chat, request, out error);
    }

    private static bool TryAddTools(JsonObject chat, JsonObject request, out string error)
    {
        error = "";
        if (chat["tools"] is not JsonArray { Count: > 0 } tools) return true;

        var mapped = new JsonArray();
        foreach (var tool in tools)
        {
            var fn = Get(tool, "function") as JsonObject;
            if (Str(Get(tool, "type")) != "function" || Str(Get(fn, "name")) is not { Length: > 0 } name)
            {
                error = "Only function tools are supported.";
                return false;
            }
            var definition = new JsonObject { ["type"] = "function", ["name"] = name };
            if (Str(Get(fn, "description")) is { } description) definition["description"] = description;
            definition["parameters"] = Get(fn, "parameters")?.DeepClone()
                ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            // Responses menganggap strict=true bila tidak disebut; Chat Completions false. Pertahankan arti klien.
            definition["strict"] = Get(fn, "strict") is JsonValue s && s.TryGetValue<bool>(out var strict) && strict;
            mapped.Add(definition);
        }
        request["tools"] = mapped;

        var choice = chat["tool_choice"];
        if (Str(choice) is "auto" or "none" or "required") request["tool_choice"] = Str(choice);
        else if (Str(Get(Get(choice, "function"), "name")) is { } forced)
            request["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = forced };
        return true;
    }

    /// <summary>
    /// String tetap string; larik bagian teks menjadi bagian <c>output_text</c> (bentuk riwayat assistant di
    /// Responses). Bagian selain teks ditolak karena riwayat assistant tidak bisa memuat gambar. Null = tidak ada isi.
    /// </summary>
    private static bool TryAssistantContent(JsonNode? content, out JsonNode? result, out string error)
    {
        result = null;
        error = "";
        if (content is null) return true;
        if (Str(content) is { } plain) { result = plain.Length > 0 ? plain : null; return true; }
        if (content is not JsonArray parts) { error = "Message content must be a string or an array of parts."; return false; }

        var mapped = new JsonArray();
        foreach (var part in parts)
        {
            if (Str(Get(part, "type")) != "text")
            {
                error = $"Only text content parts are supported in assistant history, not '{Str(Get(part, "type"))}'.";
                return false;
            }
            if (Str(Get(part, "text")) is { Length: > 0 } text) mapped.Add(new JsonObject { ["type"] = "output_text", ["text"] = text });
        }
        result = mapped.Count > 0 ? mapped : null;
        return true;
    }

    /// <summary>String tetap string; larik bagian menjadi <c>input_text</c>/<c>input_image</c>. Null = tidak ada isi.</summary>
    private static bool TryUserContent(JsonNode? content, out JsonNode? result, out string error)
    {
        result = null;
        error = "";
        if (content is null) return true;
        if (Str(content) is { } plain)
        {
            if (plain.Length > 0) result = plain;
            return true;
        }
        if (content is not JsonArray parts) { error = "Message content must be a string or an array of parts."; return false; }

        var mapped = new JsonArray();
        foreach (var part in parts)
        {
            switch (Str(Get(part, "type")))
            {
                case "text":
                    if (Str(Get(part, "text")) is { Length: > 0 } text) mapped.Add(new JsonObject { ["type"] = "input_text", ["text"] = text });
                    break;
                case "image_url":
                    var imageUrl = Str(Get(Get(part, "image_url"), "url"));
                    if (!TryImageUrl(imageUrl))
                    {
                        error = "image_url must be a base64 data URL (image/png, image/jpeg, image/gif, or image/webp) "
                            + "or an https URL; other image sources are not fetched by the gateway.";
                        return false;
                    }
                    mapped.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = imageUrl });
                    break;
                default:
                    error = $"Unsupported content part '{Str(Get(part, "type"))}'.";
                    return false;
            }
        }
        if (mapped.Count > 0) result = mapped;
        return true;
    }

    /// <summary>
    /// Data URL base64 dengan jenis gambar yang diterima vision OpenAI (png/jpeg/gif/webp), atau URL https yang
    /// diteruskan apa adanya; gateway tidak pernah mengambil gambar sendiri.
    /// </summary>
    private static bool TryImageUrl(string? url)
    {
        if (url is null) return false;
        if (url.StartsWith("https://", StringComparison.Ordinal)) return true;
        if (!url.StartsWith("data:", StringComparison.Ordinal)) return false;
        var comma = url.IndexOf(',');
        var header = comma > 5 ? url[5..comma] : "";
        return header.EndsWith(";base64", StringComparison.Ordinal) && ImageMediaTypes.Contains(header[..^7]);
    }

    /// <summary>Jenis gambar inline yang diteruskan ke Responses; selain ini ditolak klien, bukan oleh provider.</summary>
    private static readonly HashSet<string> ImageMediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public JsonObject? ToChatCompletion(JsonObject response)
    {
        if (Str(response["status"]) == "failed" || response["output"] is not JsonArray output) return null;
        var text = new StringBuilder();
        var refusal = new StringBuilder();
        var toolCalls = new JsonArray();
        foreach (var item in output)
        {
            switch (Str(Get(item, "type")))
            {
                case "message":
                    foreach (var part in Get(item, "content") as JsonArray ?? [])
                        switch (Str(Get(part, "type")))
                        {
                            case "output_text": text.Append(Str(Get(part, "text"))); break;
                            case "refusal": refusal.Append(Str(Get(part, "refusal"))); break;
                        }
                    break;
                case "function_call":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = Str(Get(item, "call_id")) ?? Str(Get(item, "id")),
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = Str(Get(item, "name")), ["arguments"] = Str(Get(item, "arguments")) ?? "{}" },
                    });
                    break;
            } // reasoning dan item lain tidak punya padanan di Chat Completions
        }

        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = toolCalls.Count > 0 && text.Length == 0 ? null : text.ToString() };
        if (refusal.Length > 0) assistant["refusal"] = refusal.ToString();
        if (toolCalls.Count > 0) assistant["tool_calls"] = toolCalls;

        var result = new JsonObject
        {
            ["id"] = Str(response["id"]),
            ["object"] = "chat.completion",
            ["created"] = LongOf(response["created_at"]) ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = Str(response["model"]),
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = assistant,
                ["finish_reason"] = FinishReason(response, toolCalls.Count > 0),
            }),
        };
        // Usage upstream tanpa satu pun counter yang dikenali tidak dikirim sebagai objek kosong berisi nol palsu.
        if (response["usage"] is JsonObject usage && UsageOf(usage) is { Count: > 0 } normalized) result["usage"] = normalized;
        return result;
    }

    /// <summary>
    /// <c>input_tokens</c> sudah mencakup token ter-cache dan <c>output_tokens</c> mencakup reasoning, seperti Chat
    /// Completions. Field yang tidak dilaporkan upstream dihilangkan, bukan ditebak sebagai nol: pencatat memakainya
    /// sebagai penanda batas bawah.
    /// </summary>
    public static JsonObject UsageOf(JsonObject usage)
    {
        var prompt = TokenCount(usage["input_tokens"]);
        var completion = TokenCount(usage["output_tokens"]);
        var result = new JsonObject();
        if (prompt is not null) result["prompt_tokens"] = prompt.Value;
        if (completion is not null) result["completion_tokens"] = completion.Value;
        long? total = (long?)TokenCount(usage["total_tokens"])
            ?? (prompt is not null && completion is not null ? (long)prompt.Value + completion.Value : null);
        if (total is not null) result["total_tokens"] = total.Value;
        if (TokenCount(Get(usage["input_tokens_details"], "cached_tokens")) is { } cached)
            result["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached };
        if (TokenCount(Get(usage["output_tokens_details"], "reasoning_tokens")) is { } reasoning)
            result["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning };
        return result;
    }

    /// <summary>
    /// <c>incomplete</c> didahulukan dari tool call (GA-10): bila respons terpotong (mis. <c>max_output_tokens</c>), argumen
    /// tool call bisa terpotong juga, jadi labelnya <c>length</c>/<c>content_filter</c>, bukan <c>tool_calls</c> yang
    /// menyiratkan panggilan lengkap dan aman dieksekusi.
    /// </summary>
    public static string FinishReason(JsonObject response, bool sawToolCall)
    {
        if (Str(response["status"]) == "incomplete")
            return Str(Get(response["incomplete_details"], "reason")) == "content_filter" ? "content_filter" : "length";
        return sawToolCall ? "tool_calls" : "stop";
    }
}
