using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Tests;

/// <summary>
/// Route ber-family anthropic_messages: klien tetap memakai Chat Completions, upstream menerima Anthropic Messages.
/// Family ditentukan katalog template provider; provider custom (tanpa template) selalu Chat Completions
/// (jalur itu sudah dicakup ProxyTests).
/// </summary>
public class AnthropicProxyTests(TestDb db) : FamilyProxyTestBase(db)
{
    private Task<Scenario> SetupAnthropicAsync() => SetupFamilyAsync(ApiFamilies.AnthropicMessages);

    [Fact]
    public async Task Translates_request_and_response_and_bills_cache_tokens_as_prompt_tokens()
    {
        var s = await SetupAnthropicAsync();
        Upstream.Respond = _ => FakeUpstream.Json(200, """
            {"id":"msg_1","type":"message","role":"assistant","model":"real-model","stop_reason":"tool_use",
             "content":[{"type":"thinking","thinking":"hm"},{"type":"text","text":"Sebentar"},
                        {"type":"tool_use","id":"toolu_1","name":"get_time","input":{"tz":"Asia/Jakarta"}}],
             "usage":{"input_tokens":10,"cache_read_input_tokens":4,"cache_creation_input_tokens":1,"output_tokens":20}}
            """);

        var response = await PostChatAsync(s.ApiKey, ToolConversation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upstream = Assert.Single(Upstream.Requests);
        Assert.Equal("https://up1.test/v1/messages", upstream.Uri.ToString());
        Assert.Equal($"Bearer {UpstreamKey}", upstream.Headers["authorization"]);
        Assert.Equal("2023-06-01", upstream.Headers["anthropic-version"]);

        var sent = JsonNode.Parse(upstream.Body)!;
        Assert.Equal(("real-model", 300, "Aturan A\n\nAturan B"), ((string?)sent["model"], (int?)sent["max_tokens"], (string?)sent["system"]));
        Assert.Equal("any", (string?)sent["tool_choice"]!["type"]);
        Assert.Equal("get_time", (string?)sent["tools"]![0]!["name"]);
        Assert.Equal("string", (string?)sent["tools"]![0]!["input_schema"]!["properties"]!["tz"]!["type"]);
        var messages = sent["messages"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, messages.Select(m => (string)m!["role"]!).ToArray());
        Assert.Equal("tool_use", (string?)messages[1]!["content"]![0]!["type"]);
        Assert.Equal("Asia/Jakarta", (string?)messages[1]!["content"]![0]!["input"]!["tz"]);
        // hasil tool dan pesan user sesudahnya berada di satu giliran user, hasil tool lebih dulu
        Assert.Equal(new[] { "tool_result", "text" }, messages[2]!["content"]!.AsArray().Select(b => (string)b!["type"]!).ToArray());

        var body = await BodyAsync(response);
        Assert.Equal("gpt-test", (string?)body["model"]);
        var choice = body["choices"]![0]!;
        Assert.Equal("tool_calls", (string?)choice["finish_reason"]);
        Assert.Equal("Sebentar", (string?)choice["message"]!["content"]);
        var call = choice["message"]!["tool_calls"]![0]!;
        Assert.Equal(("toolu_1", "get_time"), ((string?)call["id"], (string?)call["function"]!["name"]));
        Assert.Equal("Asia/Jakarta", (string?)JsonNode.Parse((string)call["function"]!["arguments"]!)!["tz"]);
        Assert.Equal((15, 20, 35, 4), ((int?)body["usage"]!["prompt_tokens"], (int?)body["usage"]!["completion_tokens"],
            (int?)body["usage"]!["total_tokens"], (int?)body["usage"]!["prompt_tokens_details"]!["cached_tokens"]));

        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((UsageStatuses.Ok, 15, 20, 4, "tool_calls"), (log.Status, log.InputTokens, log.OutputTokens, log.CachedTokens, log.FinishReason));
    }

    [Fact]
    public async Task Translates_a_stream_into_chat_chunks_with_text_tool_calls_and_usage()
    {
        var s = await SetupAnthropicAsync();
        Upstream.Respond = _ => Sse(
            Event("message_start", """{"type":"message_start","message":{"id":"msg_1","model":"real-model","usage":{"input_tokens":10,"cache_read_input_tokens":4,"output_tokens":1}}}""")
            + Event("ping", """{"type":"ping"}""")
            + Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")
            + Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ha"}}""")
            + Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}""")
            + Event("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + Event("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_time","input":{}}}""")
            + Event("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"tz\":"}}""")
            + Event("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"WIB\"}"}}""")
            + Event("content_block_stop", """{"type":"content_block_stop","index":1}""")
            + Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}""")
            + Event("message_stop", """{"type":"message_stop"}"""));

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
        Assert.Equal(("toolu_1", "get_time"), ((string?)calls[0]![0]!["id"], (string?)calls[0]![0]!["function"]!["name"]));
        Assert.Equal("{\"tz\":\"WIB\"}", string.Concat(calls.Skip(1).Select(t => (string?)t![0]!["function"]!["arguments"])));
        Assert.All(calls, t => Assert.Equal(0, (int?)t![0]!["index"]));
        Assert.Contains(chunks, c => (string?)c["choices"]!.AsArray().FirstOrDefault()?["finish_reason"] == "tool_calls");
        var last = chunks[^1];
        Assert.Empty(last["choices"]!.AsArray());
        Assert.Equal((14, 7, 4), ((int?)last["usage"]!["prompt_tokens"], (int?)last["usage"]!["completion_tokens"],
            (int?)last["usage"]!["prompt_tokens_details"]!["cached_tokens"]));

        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.SingleAsync();
        Assert.Equal((UsageStatuses.Ok, 14, 7, 4, "tool_calls"), (log.Status, log.InputTokens, log.OutputTokens, log.CachedTokens, log.FinishReason));
    }

    [Fact]
    public async Task Keeps_inline_image_parts_and_rejects_image_sources_the_gateway_would_not_forward()
    {
        var s = await SetupAnthropicAsync();
        Upstream.Respond = _ => FakeUpstream.Json(200, """
            {"id":"msg_1","type":"message","role":"assistant","model":"real-model","stop_reason":"end_turn",
             "content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}
            """);
        var png = "data:image/png;base64,iVBORw0KGgo=";

        var response = await PostChatAsync(s.ApiKey,
            $$$"""{"model":"gpt-test","messages":[{"role":"user","content":[{"type":"text","text":"baca grafik"},{"type":"image_url","image_url":{"url":"{{{png}}}"}}]}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = JsonNode.Parse(Assert.Single(Upstream.Requests).Body)!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(new[] { "text", "image" }, content.Select(b => (string)b!["type"]!).ToArray()); // urutan dan bagian utuh
        Assert.Equal("baca grafik", (string?)content[0]!["text"]);
        Assert.Equal(("base64", "image/png", "iVBORw0KGgo="),
            ((string?)content[1]!["source"]!["type"], (string?)content[1]!["source"]!["media_type"], (string?)content[1]!["source"]!["data"]));

        // Jenis gambar yang tidak didukung ditolak 400 di gateway (bukan diteruskan lalu gagal di provider).
        var rejected = await PostChatAsync(s.ApiKey,
            """{"model":"gpt-test","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/svg+xml;base64,PHN2Zz4="}}]}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("unsupported_request", ErrorCode(await BodyAsync(rejected)));
        Assert.Single(Upstream.Requests);
    }

    [Fact]
    public async Task Rejects_content_without_an_anthropic_equivalent_instead_of_dropping_it()
    {
        var s = await SetupAnthropicAsync();

        var response = await PostChatAsync(s.ApiKey,
            """{"model":"gpt-test","messages":[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"x","format":"wav"}}]}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_request", ErrorCode(await BodyAsync(response)));
        Assert.Empty(Upstream.Requests);
    }
}
