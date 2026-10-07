using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core.Auth;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Provisioning;
using AiGateway.Core.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

/// <summary>Pengganti provider upstream: merekam permintaan dan menjawab sesuai <see cref="Respond"/>.</summary>
public sealed class FakeUpstream : HttpMessageHandler
{
    public sealed record Captured(Uri Uri, HttpMethod Method, IReadOnlyDictionary<string, string> Headers, string Body);

    public const string OkBody = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"real-model","choices":[{"index":0,"finish_reason":"stop",
        "message":{"role":"assistant","content":"hi"}}],"usage":{"prompt_tokens":10,"completion_tokens":20,
        "prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":5}}}
        """;

    private readonly ConcurrentQueue<Captured> _requests = new();
    public IReadOnlyCollection<Captured> Requests => _requests;
    public Func<Captured, HttpResponseMessage> Respond { get; set; } = _ => Json(200, OkBody);

    public static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var captured = new Captured(request.RequestUri!, request.Method, headers, body);
        _requests.Enqueue(captured);
        return Respond(captured);
    }
}

/// <summary>
/// Host gateway sungguhan di atas database uji, dengan upstream palsu dan IP klien yang bisa diatur lewat header.
/// Flag rls menyalakan lapis kedua (script tenant-security.sql dipasang sebelum host start); parameter
/// authRequestsPerMinute menurunkan batas rate limit endpoint auth bila test memang mengujinya.
/// </summary>
public sealed class GatewayFactory(
    string connectionString, FakeUpstream upstream, bool rls = false, int authRequestsPerMinute = 100_000)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Satu database per test class, jadi memasang RLS di sini tidak menyentuh test class lain.
        if (rls) TestDb.InstallTenantSecurityScript(connectionString);
        builder.UseSetting("ConnectionStrings:Gateway", connectionString);
        builder.UseSetting("Seed:AdminEmail", "test-admin@example.test");
        builder.UseSetting("Seed:AdminPassword", "test-password-123456");
        builder.UseSetting("DevSeed:UpstreamApiKey", "");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-test-signing-key-0123456789");
        // Test melakukan banyak login dari 127.0.0.1; batas produksi (20/menit) tidak relevan di sini.
        builder.UseSetting("Security:AuthRequestsPerMinute", authRequestsPerMinute.ToString());
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(ChatProxy.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
            services.AddTransient<IStartupFilter, TestIpStartupFilter>();
        });
    }

    private sealed class TestIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Ip", out var ip))
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return nextMiddleware();
            });
            next(app);
        };
    }
}

public abstract class GatewayTestBase : IClassFixture<TestDb>, IDisposable
{
    protected const string UpstreamKey = "sk-upstream-secret-1234";

    protected readonly TestDb Db;
    protected readonly FakeUpstream Upstream = new();
    protected readonly GatewayFactory Factory;
    protected readonly HttpClient Client;

    protected GatewayTestBase(TestDb db, bool rls = false, int authRequestsPerMinute = 100_000)
    {
        Db = db;
        Factory = new GatewayFactory(db.ConnectionString, Upstream, rls, authRequestsPerMinute);
        Client = Factory.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    protected sealed record Scenario(long TenantId, long ProviderId, long ModelId, long ProjectId, long KeyId, string ApiKey);

    /// <summary>Tenant baru dengan satu provider custom, model "gpt-test" (harga 1/2 per 1M), project, dan API key.</summary>
    protected async Task<Scenario> SetupAsync(
        string alias = "gpt-test", string baseUrl = "https://up1.test/v1", int? maxOutput = 1000,
        DateTime? keyExpiry = null, string[]? allowedIps = null, string? authHeader = null, string? authPrefix = null,
        long? planId = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var provisioning = sp.GetRequiredService<ProvisioningService>();
        var tenant = await provisioning.CreateTenantAsync("T", Guid.NewGuid().ToString("N"), planId, default);
        sp.GetRequiredService<GatewayDbContext>().CurrentTenantId = tenant.Id;

        var provider = await provisioning.CreateCustomProviderAsync("up", baseUrl, UpstreamKey, authHeader, authPrefix, null, default);
        var model = await provisioning.CreateModelAsync(
            new ModelSpec(alias, "real-model", maxOutput, 1m, 2m), [new RouteSpec(provider.Id, "real-model")], default);
        var project = await provisioning.CreateProjectAsync("p", default);
        var key = await provisioning.CreateApiKeyAsync(project.Id, "k", keyExpiry, allowedIps, null, default);
        return new Scenario(tenant.Id, provider.Id, model.Id, project.Id, key.Entity.Id, key.PlaintextKey);
    }

    /// <summary>Jalankan operasi provisioning dalam scope baru dengan tenant yang sudah diset.</summary>
    protected async Task<T> WithTenantAsync<T>(long tenantId, Func<ProvisioningService, GatewayDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.CurrentTenantId = tenantId;
        return await action(scope.ServiceProvider.GetRequiredService<ProvisioningService>(), db);
    }

    /// <summary>Jalankan aksi dengan service apa pun dalam scope baru; tenant opsional (null = tanpa tenant).</summary>
    protected async Task<T> WithServicesAsync<T>(long? tenantId, Func<IServiceProvider, GatewayDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        if (tenantId is { } id) db.CurrentTenantId = id;
        return await action(scope.ServiceProvider, db);
    }

    protected const string StrongPassword = "correct-horse-battery-1";

    /// <summary>Tenant baru berisi satu user aktif dengan password yang sudah diatur (lewat alur undangan).</summary>
    protected async Task<(long TenantId, long UserId, string Email)> NewUserAsync(
        string role = Roles.Owner, long? tenantId = null, string password = StrongPassword)
    {
        tenantId ??= (await SetupAsync()).TenantId;
        var email = $"{Guid.NewGuid():N}@example.test";
        var userId = await WithServicesAsync(tenantId, async (sp, _) =>
        {
            var invited = await sp.GetRequiredService<UserAdminService>().InviteAsync(email, "Test User", role, default);
            await sp.GetRequiredService<AuthService>().RedeemTokenAsync(invited.InviteToken, TokenPurposes.Invite, password, default);
            return invited.User.Id;
        });
        return (tenantId.Value, userId, email);
    }

    protected async Task<HttpResponseMessage> PostChatAsync(string? apiKey, string json, Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (apiKey is not null) request.Headers.Authorization = new("Bearer", apiKey);
        tweak?.Invoke(request);
        return await Client.SendAsync(request);
    }

    protected static string Chat(string model = "gpt-test", string extra = "") =>
        $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hello"}]{{extra}}}""";

    protected static async Task<JsonNode> BodyAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    protected static string? ErrorCode(JsonNode body) => (string?)body["error"]?["code"];
}
