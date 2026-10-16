using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

/// <summary>
/// GA-06: respons SSE harus mematikan buffering server SEBELUM header dikirim. Test memasang pembungkus
/// <see cref="IHttpResponseBodyFeature"/> yang mencatat urutan <c>DisableBuffering</c> dan <c>StartAsync</c>.
/// Ini membuktikan kontrak di dalam aplikasi; perilaku nyata di belakang IIS/nginx/proxy lain tidak teruji di sini.
/// </summary>
public class SseBufferingTests(TestDb db) : GatewayTestBase(db, configureServices: services =>
{
    services.AddSingleton<BodyFeatureLog>();
    services.AddTransient<IStartupFilter, BodyFeatureSpyFilter>();
})
{
    private const string Delta = """{"id":"c1","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{"role":"assistant","content":"ha"},"finish_reason":null}]}""";
    private const string Finish = """{"id":"c2","object":"chat.completion.chunk","model":"real-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";

    private static HttpResponseMessage Sse(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };

    [Fact]
    public async Task Sse_responses_disable_server_buffering_before_the_headers_are_sent()
    {
        var s = await SetupAsync();
        Upstream.Respond = _ => Sse($"data: {Delta}\n\ndata: {Finish}\n\ndata: [DONE]\n\n");

        var response = await PostChatAsync(s.ApiKey, """{"model":"gpt-test","messages":[{"role":"user","content":"hi"}],"stream":true}""");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("data: [DONE]\n\n", raw);
        var log = Factory.Services.GetRequiredService<BodyFeatureLog>();
        var call = Assert.Single(log.Calls, c => c.Path == "/v1/chat/completions");
        Assert.True(call.DisableBufferingCalled);
        Assert.True(call.DisabledBeforeStart); // urutan: DisableBuffering lebih dulu, baru StartAsync
    }

    [Fact]
    public async Task Json_responses_do_not_touch_the_buffering_setting()
    {
        var s = await SetupAsync();

        var response = await PostChatAsync(s.ApiKey, """{"model":"gpt-test","messages":[{"role":"user","content":"hi"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var log = Factory.Services.GetRequiredService<BodyFeatureLog>();
        Assert.DoesNotContain(log.Calls, c => c.Path == "/v1/chat/completions" && c.DisableBufferingCalled);
    }
}

/// <summary>Catatan per permintaan dari <see cref="BodyFeatureSpy"/>.</summary>
public sealed class BodyFeatureLog
{
    public sealed record Call(string Path, bool DisableBufferingCalled, bool DisabledBeforeStart);

    private readonly ConcurrentQueue<Call> _calls = new();
    public IReadOnlyCollection<Call> Calls => _calls;
    public void Add(Call call) => _calls.Enqueue(call);
}

public sealed class BodyFeatureSpyFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMiddleware) =>
        {
            var inner = ctx.Features.Get<IHttpResponseBodyFeature>()!;
            var spy = new BodyFeatureSpy(inner);
            ctx.Features.Set<IHttpResponseBodyFeature>(spy);
            try { await nextMiddleware(); }
            finally { ctx.RequestServices.GetRequiredService<BodyFeatureLog>().Add(new(ctx.Request.Path, spy.DisableBufferingCalled, spy.DisabledBeforeStart)); }
        });
        next(app);
    };
}

/// <summary>Meneruskan semuanya ke fitur asli, sambil mencatat apakah buffering dimatikan sebelum respons dimulai.</summary>
public sealed class BodyFeatureSpy(IHttpResponseBodyFeature inner) : IHttpResponseBodyFeature
{
    public bool DisableBufferingCalled { get; private set; }
    public bool DisabledBeforeStart { get; private set; }

    public Stream Stream => inner.Stream;
    public PipeWriter Writer => inner.Writer;

    public void DisableBuffering()
    {
        DisableBufferingCalled = true;
        inner.DisableBuffering();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        DisabledBeforeStart = DisableBufferingCalled;
        return inner.StartAsync(cancellationToken);
    }

    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
        inner.SendFileAsync(path, offset, count, cancellationToken);

    public Task CompleteAsync() => inner.CompleteAsync();
}
