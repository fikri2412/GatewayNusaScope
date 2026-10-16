using System.Text.Json.Nodes;
using static AiGateway.Core.Proxy.JsonAccess;

namespace AiGateway.Core.Proxy;

/// <summary>
/// SSE Anthropic Messages (<c>message_start</c>, <c>content_block_*</c>, <c>message_delta</c>, <c>message_stop</c>)
/// menjadi SSE Chat Completions. Blok thinking diabaikan; event yang tidak dikenal (ping, ...) dibuang.
/// </summary>
internal sealed class AnthropicStreamReader(TextReader upstream) : ChatChunkReader(upstream)
{
    private readonly Dictionary<long, int> _toolIndexByBlock = new();
    private long? _input, _cacheRead, _cacheCreate, _output;

    protected override void OnEvent(JsonObject e)
    {
        switch (Str(e["type"]))
        {
            case "message_start":
                var message = Get(e, "message");
                Id = Str(Get(message, "id"));
                Model = Str(Get(message, "model")) ?? "";
                ReadUsage(Get(message, "usage"), final: false);
                EnsureStarted();
                break;

            case "content_block_start" when Str(Get(e["content_block"], "type")) == "tool_use":
                var tool = e["content_block"];
                var index = _toolIndexByBlock.Count;
                _toolIndexByBlock[LongOf(e["index"]) ?? -1] = index;
                Emit(Chunk(new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = index,
                        ["id"] = Str(Get(tool, "id")),
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = Str(Get(tool, "name")), ["arguments"] = "" },
                    }),
                }, null));
                break;

            case "content_block_delta":
                var delta = Get(e, "delta");
                switch (Str(Get(delta, "type")))
                {
                    case "text_delta":
                        Emit(Chunk(new JsonObject { ["content"] = Str(Get(delta, "text")) ?? "" }, null));
                        break;
                    case "input_json_delta" when _toolIndexByBlock.TryGetValue(LongOf(e["index"]) ?? -1, out var toolIndex):
                        Emit(Chunk(new JsonObject
                        {
                            ["tool_calls"] = new JsonArray(new JsonObject
                            {
                                ["index"] = toolIndex,
                                ["function"] = new JsonObject { ["arguments"] = Str(Get(delta, "partial_json")) ?? "" },
                            }),
                        }, null));
                        break;
                }
                break;

            case "message_delta":
                // GA-11: tanpa stop_reason final tidak ada alasan selesai yang dikarang ("stop" prematur): usage tetap
                // dibaca, tetapi Finish dan chunk akhir hanya ada bila upstream benar-benar menyebut alasannya.
                var reason = AnthropicAdapter.FinishReason(Str(Get(Get(e, "delta"), "stop_reason")));
                ReadUsage(Get(e, "usage"), final: reason is not null);
                if (reason is not null)
                {
                    Finish = reason;
                    Emit(Chunk(new JsonObject(), Finish));
                }
                break;

            case "message_stop":
                // Penutup sah hanya bila alasan selesai sudah terlihat; selain itu stream tetap "terputus" (tanpa [DONE]),
                // jadi pemanggil tidak menganggapnya sukses.
                if (Finish is not null) Complete();
                break;

            case "error":
                EmitError(Get(e, "error"));
                break;
        }
    }

    /// <summary>
    /// Akumulasi usage per komponen: komponen yang belum pernah dilaporkan tetap null sehingga
    /// <see cref="AnthropicAdapter.UsageOf"/> menghilangkannya (bukan nol palsu) dan
    /// <see cref="ChatChunkReader.UsageSnapshot"/> mempertahankan yang sudah diketahui bila stream terputus.
    /// </summary>
    /// <param name="final">
    /// True untuk <c>message_delta</c> yang membawa output final. Kelengkapan hanya diakui bila event itu benar-benar
    /// melaporkan <c>output_tokens</c>; output parsial dari <c>message_start</c> tidak cukup.
    /// </param>
    private void ReadUsage(JsonNode? usage, bool final)
    {
        _input = TokenCount(Get(usage, "input_tokens")) ?? _input;
        _cacheRead = TokenCount(Get(usage, "cache_read_input_tokens")) ?? _cacheRead;
        _cacheCreate = TokenCount(Get(usage, "cache_creation_input_tokens")) ?? _cacheCreate;
        var output = TokenCount(Get(usage, "output_tokens"));
        _output = output ?? _output;
        var reported = new JsonObject();
        if (_input is { } input) reported["input_tokens"] = input;
        if (_cacheRead is { } cacheRead) reported["cache_read_input_tokens"] = cacheRead;
        if (_cacheCreate is { } cacheCreate) reported["cache_creation_input_tokens"] = cacheCreate;
        if (_output is { } accumulated) reported["output_tokens"] = accumulated;
        var normalized = AnthropicAdapter.UsageOf(reported);
        if (normalized.Count > 0) MergeReportedUsage(normalized);
        if (final && output is not null) UsageIncomplete = false;
    }
}
