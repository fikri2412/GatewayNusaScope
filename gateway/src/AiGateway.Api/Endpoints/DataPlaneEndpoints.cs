using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Data;
using AiGateway.Core.Options;
using AiGateway.Core.Proxy;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiGateway.Api.Endpoints;

/// <summary>Data plane: dipanggil aplikasi pelanggan dengan API key, format OpenAI.</summary>
public static class DataPlaneEndpoints
{
    private const string TagsHeader = "X-Gateway-Tags";

    public static IEndpointRouteBuilder MapDataPlane(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");
        v1.MapPost("/chat/completions", ChatCompletions);
        v1.MapGet("/models", ListModels);
        return app;
    }

    private static async Task<IResult> ChatCompletions(
        HttpContext http, ApiKeyAuthenticator auth, UsageRecorder recorder, ChatProxy proxy,
        IOptions<ProxyOptions> options, CancellationToken ct)
    {
        var (caller, failure) = await AuthenticateAsync(http, auth, recorder, ct);
        if (caller is null) return failure!;

        var limit = options.Value.MaxRequestBytes;
        if (http.Request.ContentLength > limit)
            return Write(http, await DeniedAsync(recorder, caller, GatewayResponse.Error(
                413, ErrorTypes.InvalidRequest, "request_too_large", $"Request body exceeds {limit} bytes."), "request_too_large"));
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit;

        JsonNode? payload = null;
        try { payload = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct); }
        catch (JsonException) { /* payload null -> ditolak ChatProxy sebagai invalid_json */ }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Write(http, await DeniedAsync(recorder, caller, GatewayResponse.Error(
                413, ErrorTypes.InvalidRequest, "request_too_large", $"Request body exceeds {limit} bytes."), "request_too_large"));
        }

        return Write(http, await proxy.HandleAsync(caller, payload, ct));
    }

    private static async Task<IResult> ListModels(
        HttpContext http, ApiKeyAuthenticator auth, UsageRecorder recorder, GatewayDbContext db, CancellationToken ct)
    {
        var (caller, failure) = await AuthenticateAsync(http, auth, recorder, ct);
        if (caller is null) return failure!;

        var models = await db.Models.AsNoTracking().Where(m => m.Enabled).OrderBy(m => m.Alias)
            .Select(m => new { m.Alias, m.CreatedAt }).ToListAsync(ct);
        var body = JsonSerializer.Serialize(new
        {
            @object = "list",
            data = models.Select(m => new
            {
                id = m.Alias,
                @object = "model",
                created = new DateTimeOffset(DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                owned_by = "gateway",
            }),
        });
        return Write(http, new GatewayResponse(200, body));
    }

    /// <summary>Autentikasi + header X-Request-Id. Penolakan setelah key valid ikut dicatat sebagai usage.</summary>
    private static async Task<(GatewayCaller? Caller, IResult? Failure)> AuthenticateAsync(
        HttpContext http, ApiKeyAuthenticator auth, UsageRecorder recorder, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        http.Response.Headers["X-Request-Id"] = requestId.ToString();

        var result = await auth.AuthenticateAsync(
            http.Request.Headers.Authorization, http.Connection.RemoteIpAddress,
            http.Request.Headers.UserAgent, http.Request.Headers[TagsHeader], requestId, ct);
        if (result.Failure is null) return (result.Caller, null);

        if (result.Caller is not null)
            await recorder.RecordDeniedAsync(result.Caller, result.Failure.Status, result.DeniedReason!);
        return (null, Write(http, result.Failure));
    }

    private static async Task<GatewayResponse> DeniedAsync(
        UsageRecorder recorder, GatewayCaller caller, GatewayResponse response, string reason)
    {
        await recorder.RecordDeniedAsync(caller, response.Status, reason);
        return response;
    }

    private static IResult Write(HttpContext http, GatewayResponse response)
    {
        if (response.RetryAfter is not null) http.Response.Headers.RetryAfter = response.RetryAfter;
        return Results.Text(response.Body, "application/json", Encoding.UTF8, response.Status);
    }
}
