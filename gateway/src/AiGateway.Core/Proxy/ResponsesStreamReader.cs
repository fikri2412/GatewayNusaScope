using System.Text.Json.Nodes;
using static AiGateway.Core.Proxy.JsonAccess;

namespace AiGateway.Core.Proxy;

/// <summary>
/// SSE OpenAI Responses (<c>response.created</c>, <c>response.output_text.delta</c>,
/// <c>response.function_call_arguments.delta</c>, <c>response.completed</c>, ...) menjadi SSE Chat Completions.
/// Event reasoning dan event lain yang tidak dikenal dibuang.
/// </summary>
internal sealed class ResponsesStreamReader(TextReader upstream) : ChatChunkReader(upstream)
{
    private readonly Dictionary<long, int> _toolIndexByOutput = new();

    protected override void OnEvent(JsonObject e)
    {
        // Usage bisa ikut di beberapa event respons; simpan laporan terakhir yang berisi angka, sehingga stream yang
        // terputus sebelum event final tetap mempertahankan usage yang sudah diketahui. Laporan rusak/kosong tidak
        // menghapus angka yang sudah ada.
        if (Get(e, "response") is JsonObject payload && payload["usage"] is JsonObject reported)
        {
            var normalized = ResponsesAdapter.UsageOf(reported);
            if (normalized.Count > 0) MergeReportedUsage(normalized);
            // Kelengkapan hanya diakui bila event final benar-benar melaporkan output_tokens.
            if (Str(e["type"]) is "response.completed" or "response.incomplete" && TokenCount(reported["output_tokens"]) is not null)
                UsageIncomplete = false;
        }

        switch (Str(e["type"]))
        {
            case "response.created":
                var response = e["response"];
                Id = Str(Get(response, "id"));
                Model = Str(Get(response, "model")) ?? "";
                EnsureStarted();
                break;

            case "response.output_item.added" when Str(Get(e["item"], "type")) == "function_call":
                EnsureStarted();
                var item = e["item"];
                var index = _toolIndexByOutput.Count;
                _toolIndexByOutput[LongOf(e["output_index"]) ?? -1] = index;
                Emit(Chunk(new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = index,
                        ["id"] = Str(Get(item, "call_id")) ?? Str(Get(item, "id")),
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = Str(Get(item, "name")), ["arguments"] = "" },
                    }),
                }, null));
                break;

            case "response.output_text.delta":
                EnsureStarted();
                Emit(Chunk(new JsonObject { ["content"] = Str(e["delta"]) ?? "" }, null));
                break;

            case "response.refusal.delta":
                // GA-09: penolakan model mengalir sebagai delta.refusal (seperti Chat Completions), bukan jawaban kosong.
                EnsureStarted();
                Emit(Chunk(new JsonObject { ["refusal"] = Str(e["delta"]) ?? "" }, null));
                break;

            case "response.function_call_arguments.delta" when _toolIndexByOutput.TryGetValue(LongOf(e["output_index"]) ?? -1, out var toolIndex):
                Emit(Chunk(new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = toolIndex,
                        ["function"] = new JsonObject { ["arguments"] = Str(e["delta"]) ?? "" },
                    }),
                }, null));
                break;

            case "response.completed" or "response.incomplete":
                EnsureStarted();
                var final = e["response"] as JsonObject ?? new JsonObject();
                var sawToolCall = _toolIndexByOutput.Count > 0
                    || (final["output"] as JsonArray)?.Any(i => Str(Get(i, "type")) == "function_call") == true;
                Finish = ResponsesAdapter.FinishReason(final, sawToolCall);
                Emit(Chunk(new JsonObject(), Finish));
                Complete();
                break;

            case "response.failed":
                EmitError(Get(e["response"], "error"));
                break;

            case "error":
                EmitError(new JsonObject { ["message"] = Str(e["message"]), ["code"] = Str(e["code"]), ["type"] = "upstream_error" });
                break;
        }
    }
}
