using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;
using AiGateway.Core.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class StreamReliabilityTests(TestDb db) : GatewayTestBase(db, configureServices: services =>
    services.Configure<AiGateway.Core.Options.ProxyOptions>(o =>
    {
        o.UpstreamTimeoutSeconds = 1;
        o.MaxStreamSeconds = 2;
        o.MaxConcurrentPerTenant = 1;
    }))
{
    private const string Content = "data: {\"model\":\"real-model\",\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\n";
    private const string Usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}\n\n";
    private const string Error = "data: {\"error\":{\"code\":\"provider_failed\",\"message\":\"failed\"}}\n\n";

    [Fact]
    public async Task Total_deadline_cancels_stalled_downstream_and_releases_the_slot()
    {
        var s = await SetupAsync();
        Upstream.Respond = _ => Sse(Content + Usage + "data: [DONE]\n\n");
        using var external = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var output = new StalledOutput(blockFirstFlush: true, disconnect: external.Cancel);
        var elapsed = Stopwatch.StartNew();
        await PumpAsync(s, output, external.Token);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(6), $"Downstream remained blocked for {elapsed.Elapsed}.");
        await AssertFailureAndReleasedAsync(s, "upstream_stream_timeout");
    }

    [Fact]
    public async Task Error_notification_after_idle_timeout_cannot_hold_the_slot_past_total_deadline()
    {
        var s = await SetupAsync();
        Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PrefixThenStall(Content)),
        };
        using var output = new StalledOutput(blockFirstFlush: false);
        using var external = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var elapsed = Stopwatch.StartNew();
        await PumpAsync(s, output, external.Token, fixContentType: true);
        Assert.True(output.Flushes >= 2, "Expected an initial content flush and a terminal-error flush.");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(6), $"Terminal error flush remained blocked for {elapsed.Elapsed}.");
        await AssertFailureAndReleasedAsync(s, "upstream_stream_timeout");
    }

    [Fact]
    public async Task Error_followed_by_done_is_failure_without_retry_or_success_terminator()
    {
        var s = await SetupAsync();
        Upstream.Respond = _ => Sse(Content + Usage + Error + "data: [DONE]\n\n");
        using var response = await PostChatAsync(s.ApiKey, Chat(extra: ",\"stream\":true"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("provider_failed", text);
        Assert.DoesNotContain("[DONE]", text);
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.AsNoTracking().SingleAsync();
        Assert.Equal((UsageStatuses.Error, 200, "upstream_stream_error", 1), (log.Status, log.HttpStatus, log.DeniedReason, log.Attempts));
        Assert.Equal((10, 3), (log.InputTokens, log.OutputTokens));
        Assert.Single(Upstream.Requests);
        var daily = await ctx.UsageDailies.AsNoTracking().SingleAsync();
        Assert.Equal((1L, 1L, 10L, 3L), (daily.Requests, daily.Errors, daily.InputTokens, daily.OutputTokens));
    }

    [Fact]
    public async Task Malformed_final_usage_preserves_known_counts_but_marks_the_record_incomplete()
    {
        var s = await SetupAsync();
        Upstream.Respond = _ => Sse(Content + Usage
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":\"bad\",\"completion_tokens\":-1}}\n\n"
            + "data: [DONE]\n\n");
        using var response = await PostChatAsync(s.ApiKey, Chat(extra: ",\"stream\":true"));
        Assert.Contains("[DONE]", await response.Content.ReadAsStringAsync());
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.AsNoTracking().SingleAsync();
        Assert.Equal((10, 3, true), (log.InputTokens, log.OutputTokens, log.UsageIncomplete));
        var daily = await ctx.UsageDailies.AsNoTracking().SingleAsync();
        Assert.Equal((10L, 3L, 1L), (daily.InputTokens, daily.OutputTokens, daily.IncompleteUsageRequests));
    }

    [Fact]
    public async Task Client_cancellation_during_downstream_flush_remains_499()
    {
        var s = await SetupAsync();
        Upstream.Respond = _ => Sse(Content + "data: [DONE]\n\n");
        using var output = new StalledOutput(blockFirstFlush: true);
        using var external = new CancellationTokenSource();
        var pumping = PumpAsync(s, output, external.Token);
        await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        external.Cancel();
        await pumping.WaitAsync(TimeSpan.FromSeconds(10));
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.AsNoTracking().SingleAsync();
        Assert.Equal((UsageStatuses.Error, 499), (log.Status, log.HttpStatus));
    }

    private async Task PumpAsync(Scenario s, Stream output, CancellationToken ct, bool fixContentType = false)
    {
        if (fixContentType)
        {
            var respond = Upstream.Respond;
            Upstream.Respond = request =>
            {
                var response = respond(request);
                response.Content.Headers.ContentType = new("text/event-stream");
                return response;
            };
        }
        await WithServicesAsync(s.TenantId, async (sp, ctx) =>
        {
            var key = await ctx.ApiKeys.SingleAsync(k => k.Id == s.KeyId);
            var response = await sp.GetRequiredService<ChatProxy>().HandleAsync(
                new GatewayCaller(key, Guid.NewGuid(), null, null, null),
                JsonNode.Parse(Chat(extra: ",\"stream\":true")), ct);
            Assert.NotNull(response.StreamBody);
            await response.StreamBody(output, ct);
            return 0;
        });
    }

    private async Task AssertFailureAndReleasedAsync(Scenario s, string reason)
    {
        await using var ctx = Db.NewContext(s.TenantId);
        var log = await ctx.UsageLogs.AsNoTracking().SingleAsync();
        Assert.Equal((UsageStatuses.Error, 200, reason), (log.Status, log.HttpStatus, log.DeniedReason));
        Upstream.Respond = _ => FakeUpstream.Json(200, FakeUpstream.OkBody);
        using var healthy = await PostChatAsync(s.ApiKey, Chat());
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal("hi", (string?)(await BodyAsync(healthy))["choices"]![0]!["message"]!["content"]);
    }

    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/event-stream"),
    };

    private sealed class StalledOutput(bool blockFirstFlush, Action? disconnect = null) : MemoryStream
    {
        public int Flushes { get; private set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            if (!blockFirstFlush && Flushes == 1) return;
            Blocked.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException ex)
            {
                disconnect?.Invoke(); // Kestrel abort side-effect must not relabel the gateway deadline as 499.
                throw new IOException("Socket closed during flush.", ex);
            }
        }
    }

    private sealed class PrefixThenStall(string prefix) : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
