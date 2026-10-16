using System.Net;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Route ber-family openai_responses: klien tetap memakai Chat Completions, upstream menerima OpenAI Responses.
/// </summary>
public class ResponsesProxyTests(TestDb db) : FamilyProxyTestBase(db)
{
    private Task<Scenario> SetupResponsesAsync() => SetupFamilyAsync(ApiFamilies.OpenAiResponses);

    [Fact]
    public async Task Translates_request_and_response_and_reads_usage_from_responses_fields()
    {
        var s = await SetupResponsesAsync();
        Upstream.Respond = _ => FakeUpstream.Json(200, """
            {"id":"resp_1","object":"response","created_at":1700000000,"status":"completed","model":"real-model",
             "output":[{"type":"reasoning","id":"rs_1","summary":[]},
                       {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Sebentar"}]},
                       {"type":"function_call","id":"fc_1","call_id":"call_9","name":"get_time","arguments":"{\"tz\":\"Asia/Jakarta\"}"}],
             "usage":{"input_tokens":15,"input_tokens_details":{"cached_tokens":4},"output_tokens":20,
                      "output_tokens_details":{"reasoning_tokens":5},"total_tokens":35}}
            """);

        var response = await PostChatAsync(s.ApiKey, ToolConversation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upstream = Assert.Single(Upstream.Requests);
        Assert.Equal("https://up1.test/v1/responses", upstream.Uri.ToString());
        Assert.Equal($"Bearer {UpstreamKey}", upstream.Headers["authorization"]);

        var sent = JsonNode.Parse(upstream.Body)!;
        Assert.Equal(("real-model", false, 300, "Aturan A\n\nAturan B", "required"),
            ((string?)sent["model"], (bool?)sent["store"], (int?)sent["max_output_tokens"], (string?)sent["instructions"], (string?)sent["tool_choice"]));
        var tool = sent["tools"]![0]!;
        Assert.Equal(("function", "get_time", false), ((string?)tool["type"], (string?)tool["name"], (bool?)tool["strict"])); // strict=false seperti Chat Completions
        Assert.Equal("string", (string?)tool["parameters"]!["properties"]!["tz"]!["type"]);
        var input = sent["input"]!.AsArray();
        Assert.Equal(new[] { "user", "function_call", "function_call_output", "user" },
            input.Select(i => (string)(i!["type"] ?? i["role"])!).ToArray());
        Assert.Equal(("call_1", "{\"tz\":\"Asia/Jakarta\"}"), ((string?)input[1]!["call_id"], (string?)input[1]!["arguments"])); // arguments tetap string
        Assert.Equal(("call_1", "10:00"), ((string?)input[2]!["call_id"], (string?)input[2]!["output"]));

        var body = await BodyAsync(response);
        Assert.Equal("gpt-test", (string?)body["model"]);
        var choice = body["choices"]![0]!;
        Assert.Equal(("tool_calls", "Sebentar"), ((string?)choice["finish_reason"], (string?)choice["message"]!["content"]));
        var call = choice["message"]!["tool_calls"]![0]!;
        Assert.Equal(("call_9", "get_time"), ((string?)call["id"], (string?)call["function"]!["name"]));
        Assert.Equal("Asia/Jakarta", (string?)JsonNode.Parse((string)call["function"]!["arguments"]!)!["tz"]);
        Assert.Equal((15, 20, 35, 4), ((int?)body["usage"]!["prompt_tokens"], (int?)body["usage"]!["completion_tokens"],
            (int?)body["usage"]!["total_tokens"], (int?)body["usage"]!["prompt_tokens_details"]!["cached_tokens"]));

        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((UsageStatuses.Ok, 15, 20, 4, 5, "tool_calls"),
            (log.Status, log.InputTokens, log.OutputTokens, log.CachedTokens, log.ReasoningTokens, log.FinishReason));
    }

    [Fact]
    public async Task Translates_a_stream_into_chat_chunks_with_text_tool_calls_and_usage()
    {
        var s = await SetupResponsesAsync();
        Upstream.Respond = _ => Sse(
            Event("response.created", """{"type":"response.created","response":{"id":"resp_1","model":"real-model","status":"in_progress"}}""")
            + Event("response.in_progress", """{"type":"response.in_progress","response":{"id":"resp_1"}}""")
            + Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}""")
            + Event("response.output_item.added", """{"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant"}}""")
            + Event("response.output_text.delta", """{"type":"response.output_text.delta","output_index":1,"content_index":0,"delta":"ha"}""")
            + Event("response.output_text.delta", """{"type":"response.output_text.delta","output_index":1,"content_index":0,"delta":"lo"}""")
            + Event("response.output_item.added", """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_9","name":"get_time","arguments":""}}""")
            + Event("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"tz\":"}""")
            + Event("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"\"WIB\"}"}""")
            + Event("response.completed", """
                {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"type":"function_call","call_id":"call_9","name":"get_time","arguments":"{}"}],
                 "usage":{"input_tokens":14,"input_tokens_details":{"cached_tokens":4},"output_tokens":7,"output_tokens_details":{"reasoning_tokens":2},"total_tokens":21}}}
                """.ReplaceLineEndings("").Replace("  ", "")));

        var response = await PostChatAsync(s.ApiKey,
            Chat(extra: ",\"stream\":true,\"stream_options\":{\"include_usage\":true},\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get_time\"}}]"));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.True((bool?)JsonNode.Parse(Assert.Single(Upstream.Requests).Body)!["stream"]);
        Assert.EndsWith("data: [DONE]\n\n", raw);

        var chunks = raw.Split('\n').Where(l => l.StartsWith("data: ") && l != "data: [DONE]").Select(l => JsonNode.Parse(l[6..])!).ToList();
        Assert.All(chunks, c => Assert.Equal("gpt-test", (string?)c["model"]));
        Assert.Equal("assistant", (string?)chunks[0]["choices"]![0]!["delta"]!["role"]);
        Assert.Equal("halo", string.Concat(chunks.Select(c => (string?)c["choices"]!.AsArray().FirstOrDefault()?["delta"]?["content"])));
        var calls = chunks.Select(c => c["choices"]!.AsArray().FirstOrDefault()?["delta"]?["tool_calls"]).Where(t => t is not null).ToList();
        Assert.Equal(("call_9", "get_time"), ((string?)calls[0]![0]!["id"], (string?)calls[0]![0]!["function"]!["name"]));
        Assert.Equal("{\"tz\":\"WIB\"}", string.Concat(calls.Skip(1).Select(t => (string?)t![0]!["function"]!["arguments"])));
        Assert.All(calls, t => Assert.Equal(0, (int?)t![0]!["index"])); // indeks tool dihitung sendiri, bukan output_index upstream
        Assert.Contains(chunks, c => (string?)c["choices"]!.AsArray().FirstOrDefault()?["finish_reason"] == "tool_calls");
        var last = chunks[^1];
        Assert.Empty(last["choices"]!.AsArray());
        Assert.Equal((14, 7, 4), ((int?)last["usage"]!["prompt_tokens"], (int?)last["usage"]!["completion_tokens"],
            (int?)last["usage"]!["prompt_tokens_details"]!["cached_tokens"]));

        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((UsageStatuses.Ok, 14, 7, 4, 2, "tool_calls"),
            (log.Status, log.InputTokens, log.OutputTokens, log.CachedTokens, log.ReasoningTokens, log.FinishReason));
    }

    [Fact]
    public async Task Streams_a_refusal_as_delta_refusal_instead_of_an_empty_answer()
    {
        var s = await SetupResponsesAsync();
        Upstream.Respond = _ => Sse(
            Event("response.created", """{"type":"response.created","response":{"id":"resp_1","model":"real-model","status":"in_progress"}}""")
            + Event("response.refusal.delta", """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"Maaf, "}""")
            + Event("response.refusal.delta", """{"type":"response.refusal.delta","output_index":0,"content_index":0,"delta":"tidak bisa."}""")
            + Event("response.completed", """{"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":4}}}"""));

        var response = await PostChatAsync(s.ApiKey, Chat(extra: ",\"stream\":true,\"stream_options\":{\"include_usage\":true}"));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.EndsWith("data: [DONE]\n\n", raw);
        var chunks = raw.Split('\n').Where(l => l.StartsWith("data: ") && l != "data: [DONE]").Select(l => JsonNode.Parse(l[6..])!).ToList();
        var deltas = chunks.Select(c => c["choices"]!.AsArray().FirstOrDefault()?["delta"]).Where(d => d is not null).ToList();
        Assert.Equal("Maaf, tidak bisa.", string.Concat(deltas.Select(d => (string?)d!["refusal"])));
        Assert.DoesNotContain(deltas, d => !string.IsNullOrEmpty((string?)d!["content"])); // bukan jawaban teks
    }

    [Theory]
    [InlineData("max_output_tokens", "length")]
    [InlineData("content_filter", "content_filter")]
    public async Task An_incomplete_response_wins_over_tool_calls_in_the_finish_reason(string reason, string expected)
    {
        var s = await SetupResponsesAsync();
        Upstream.Respond = _ => FakeUpstream.Json(200, $$$"""
            {"id":"resp_1","object":"response","created_at":1700000000,"status":"incomplete","incomplete_details":{"reason":"{{{reason}}}"},"model":"real-model",
             "output":[{"type":"function_call","id":"fc_1","call_id":"call_9","name":"get_time","arguments":"{\"tz\":\"Asia/Jak"}],
             "usage":{"input_tokens":15,"output_tokens":20}}
            """);

        var response = await PostChatAsync(s.ApiKey, ToolConversation);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (string?)body["choices"]![0]!["finish_reason"]); // bukan tool_calls: argumennya terpotong
        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Equal(expected, (await ctx.UsageLogs.SingleAsync()).FinishReason);
    }

    [Fact]
    public async Task A_streamed_incomplete_response_with_a_tool_call_is_labelled_length()
    {
        var s = await SetupResponsesAsync();
        Upstream.Respond = _ => Sse(
            Event("response.created", """{"type":"response.created","response":{"id":"resp_1","model":"real-model","status":"in_progress"}}""")
            + Event("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_9","name":"get_time","arguments":""}}""")
            + Event("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"tz\":\"Asia/Jak"}""")
            + Event("response.incomplete", """{"type":"response.incomplete","response":{"id":"resp_1","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"function_call","call_id":"call_9","name":"get_time","arguments":"{\"tz\":\"Asia/Jak"}],"usage":{"input_tokens":14,"output_tokens":7}}}"""));

        var response = await PostChatAsync(s.ApiKey, Chat(extra: ",\"stream\":true,\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get_time\"}}]"));
        var raw = await response.Content.ReadAsStringAsync();

        var chunks = raw.Split('\n').Where(l => l.StartsWith("data: ") && l != "data: [DONE]").Select(l => JsonNode.Parse(l[6..])!).ToList();
        var finishes = chunks.Select(c => (string?)c["choices"]!.AsArray().FirstOrDefault()?["finish_reason"]).Where(f => f is not null).ToList();
        Assert.Equal(new[] { "length" }, finishes.ToArray());
        await using var ctx = Db.NewContext(s.TenantId);
        Assert.Equal("length", (await ctx.UsageLogs.SingleAsync()).FinishReason);
    }

    [Fact]
    public async Task Keeps_inline_image_parts_and_assistant_history_parts_instead_of_flattening_them()
    {
        var s = await SetupResponsesAsync();
        Upstream.Respond = _ => FakeUpstream.Json(200, """
            {"id":"resp_1","object":"response","status":"completed","model":"real-model",
             "output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}],
             "usage":{"input_tokens":3,"output_tokens":1,"total_tokens":4}}
            """);
        var jpeg = "data:image/jpeg;base64,/9j/4AAQ";

        var response = await PostChatAsync(s.ApiKey,
            $$$"""
            {"model":"gpt-test","messages":[
                {"role":"assistant","content":[{"type":"text","text":"bagian satu"},{"type":"text","text":"bagian dua"}]},
                {"role":"user","content":[{"type":"text","text":"lihat grafik"},{"type":"image_url","image_url":{"url":"{{{jpeg}}}"}}]}]}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var input = JsonNode.Parse(Assert.Single(Upstream.Requests).Body)!["input"]!.AsArray();
        // Riwayat assistant tetap berupa bagian output_text terpisah, bukan satu string yang digabung.
        Assert.Equal("assistant", (string?)input[0]!["role"]);
        var said = input[0]!["content"]!.AsArray();
        Assert.All(said, part => Assert.Equal("output_text", (string?)part!["type"]));
        Assert.Equal(new[] { "bagian satu", "bagian dua" }, said.Select(part => (string)part!["text"]!).ToArray());
        // Pesan user: teks dan gambar berdampingan seperti aslinya.
        var parts = input[1]!["content"]!.AsArray();
        Assert.Equal(new[] { "input_text", "input_image" }, parts.Select(part => (string)part!["type"]!).ToArray());
        Assert.Equal(("lihat grafik", jpeg), ((string?)parts[0]!["text"], (string?)parts[1]!["image_url"]));

        // Bagian gambar yang tidak didukung ditolak 400 di gateway, bukan diteruskan lalu gagal di provider.
        var rejected = await PostChatAsync(s.ApiKey,
            """{"model":"gpt-test","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/svg+xml;base64,PHN2Zz4="}}]}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("unsupported_request", ErrorCode(await BodyAsync(rejected)));
        Assert.Single(Upstream.Requests);
    }

    [Fact]
    public async Task Rejects_content_without_a_responses_equivalent_instead_of_dropping_it()
    {
        var s = await SetupResponsesAsync();

        var response = await PostChatAsync(s.ApiKey,
            """{"model":"gpt-test","messages":[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"x","format":"wav"}}]}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_request", ErrorCode(await BodyAsync(response)));
        Assert.Empty(Upstream.Requests);
    }

    [Fact]
    public async Task A_stream_whose_first_event_is_an_error_is_a_failed_attempt_not_a_committed_stream()
    {
        var s = await SetupResponsesAsync();
        Upstream.Respond = _ => Sse(Event("error", """{"type":"error","code":"server_error","message":"boom"}"""));

        var response = await PostChatAsync(s.ApiKey, Chat(extra: ",\"stream\":true"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); // belum ada byte stream yang dikirim ke klien
        Assert.Equal("upstream_error", ErrorCode(await BodyAsync(response)));
    }
}
