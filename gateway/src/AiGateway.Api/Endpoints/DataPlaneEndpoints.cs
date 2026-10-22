using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Options;
using AiGateway.Core.Proxy;
using Microsoft.AspNetCore.Http.Features;
using AiGateway.Core.Security;
using AiGateway.Core.WebSearch;
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
        v1.MapPost("/search", SearchAsync);
        v1.MapPost("/extract", ExtractAsync);
        return app;
    }

    private static async Task<IResult> ChatCompletions(
        HttpContext http, ApiKeyAuthenticator auth, UsageRecorder recorder, ChatProxy proxy,
        IOptions<ProxyOptions> options, CancellationToken ct)
    {
        var (caller, failure) = await AuthenticateAsync(http, auth, recorder, ct);
        if (caller is null) return failure!;
        var sessionHeader = http.Request.Headers["x-opencode-session"];
        if (sessionHeader.Count > 0)
        {
            var sessionId = sessionHeader[0];
            if (sessionHeader.Count != 1 || !SessionIds.IsValid(sessionId))
                return Write(http, await DeniedAsync(recorder, caller, GatewayResponse.Error(
                    400, ErrorTypes.InvalidRequest, "invalid_session_id", "Session ID must contain 1-200 letters, digits, '.', '_' or '-'."), "invalid_session_id"));
            caller = caller with { SessionId = sessionId };
        }


        var limit = options.Value.MaxRequestBytes;
        Task<GatewayResponse> TooLargeAsync() => DeniedAsync(recorder, caller, GatewayResponse.Error(
            413, ErrorTypes.InvalidRequest, "request_too_large", $"Request body exceeds {limit} bytes."), "request_too_large");
        if (http.Request.ContentLength > limit)
            return Write(http, await TooLargeAsync());
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit;

        JsonNode? payload = null;
        try
        {
            // Batas juga ditegakkan di tingkat byte: request chunked tidak punya Content-Length dan host tanpa
            // IHttpMaxRequestBodySizeFeature yang bisa disetel (mis. IIS) tidak menegakkan batas yang dikonfigurasi.
            await using var bounded = new LimitedReadStream(http.Request.Body, limit);
            payload = await JsonNode.ParseAsync(bounded, cancellationToken: ct);
        }
        catch (JsonException) { /* payload null -> ditolak ChatProxy sebagai invalid_json */ }
        catch (ResponseTooLargeException) { return Write(http, await TooLargeAsync()); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Write(http, await TooLargeAsync());
        }

        return Write(http, await proxy.HandleAsync(caller, payload, ct));
    }

    private static async Task<IResult> ListModels(
        HttpContext http, ApiKeyAuthenticator auth, UsageRecorder recorder, GatewayDbContext db,
        PolicyEngine policy, CancellationToken ct)
    {
        var (caller, failure) = await AuthenticateAsync(http, auth, recorder, ct);
        if (caller is null) return failure!;

        // Daftar model kebijakan berlaku di sini juga: yang ditawarkan harus sama dengan yang boleh dipanggil.
        var policies = await policy.ActivePoliciesAsync(caller, ct);
        var models = await db.Models.AsNoTracking().Where(m => m.Enabled).OrderBy(m => m.Alias)
            .Select(m => new { m.Id, m.Alias, m.CreatedAt, m.SupportsTools, m.SupportsStreaming, m.SupportsVision, m.MaxOutputTokens }).ToListAsync(ct);
        var allowed = models.Where(m => PolicyEngine.DeniedModelScope(policies, m.Alias) is null).ToList();
        // Siap dipakai = eligibilitas forwarding yang sama dengan ChatProxy, bukan kesehatan upstream.
        var targets = await ForwardingTargets.LoadAsync(db, allowed.Select(m => m.Id).ToList(), ct);
        var policyLimit = PolicyEngine.MaxRequestTokens(policies);
        var body = JsonSerializer.Serialize(new
        {
            @object = "list",
            data = allowed.Select(m =>
            {
                var available = targets.CanForward(m.Id);
                return new
                {
                    id = m.Alias,
                    @object = "model",
                    created = new DateTimeOffset(DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                    owned_by = "gateway",
                    // NULL = belum dikonfirmasi admin; klien memutuskan sendiri kelayakannya.
                    capabilities = new { tools = m.SupportsTools, streaming = m.SupportsStreaming, vision = m.SupportsVision },
                    readiness = new { available, reason = available ? null : "no_available_provider" },
                    limits = new { max_output_tokens = EffectiveLimit(m.MaxOutputTokens, policyLimit) },
                };
            }),
        });
        return Write(http, new GatewayResponse(200, body));
    }
    private const int QueryMaxLength = 500;
    private const int MaxResultsCap = 10;
    private const int MaxExtractUrls = 5;
    private const int MaxUrlLength = 2048;

    private static async Task<IResult> SearchAsync(
        HttpContext http, ApiKeyAuthenticator auth, UsageRecorder recorder, WebSearchHub hub,
        RequestRateLimiter limiter, PolicyEngine policy,
        IOptions<ProxyOptions> options, IOptions<WebSearchOptions> searchOptions, CancellationToken ct)
    {
        var (caller, failure) = await AuthenticateAsync(http, auth, recorder, ct);
        if (caller is null) return failure!;
        if (await SearchRateLimitAsync(http, caller, policy, limiter, searchOptions, ct) is { } limited) return limited;

        var limit = options.Value.MaxRequestBytes;
        if (http.Request.ContentLength > limit)
            return Problem(413, "request_too_large", $"Request body exceeds {limit} bytes.");

        SearchRequest? request = null;
        try
        {
            await using var bounded = new LimitedReadStream(http.Request.Body, limit);
            request = await JsonSerializer.DeserializeAsync<SearchRequest>(bounded, JsonSerializerOptions.Web, cancellationToken: ct);
        }
        catch (JsonException) { return Problem(400, "invalid_json", "Body JSON tidak valid."); }
        catch (ResponseTooLargeException) { return Problem(413, "request_too_large", $"Request body exceeds {limit} bytes."); }

        if (request is null) return Problem(400, "invalid_body", "Body permintaan kosong.");
        if (string.IsNullOrWhiteSpace(request.Query)) return Problem(400, "invalid_query", "Query pencarian wajib diisi.");
        if (request.Query.Length > QueryMaxLength) return Problem(400, "query_too_long", $"Query maksimal {QueryMaxLength} karakter.");
        request = request with { MaxResults = Math.Clamp(request.MaxResults, 1, MaxResultsCap) };

        try
        {
            return Results.Ok(await hub.SearchAsync(request, ct));
        }
        catch (GatewayException ex)
        {
            return GatewayError(http, ex);
        }
    }

    private static async Task<IResult> ExtractAsync(
        HttpContext http, ApiKeyAuthenticator auth, UsageRecorder recorder, WebSearchHub hub,
        RequestRateLimiter limiter, PolicyEngine policy, OutboundSecurityPolicy outbound,
        IOptions<ProxyOptions> options, IOptions<WebSearchOptions> searchOptions, CancellationToken ct)
    {
        var (caller, failure) = await AuthenticateAsync(http, auth, recorder, ct);
        if (caller is null) return failure!;
        if (await SearchRateLimitAsync(http, caller, policy, limiter, searchOptions, ct) is { } limited) return limited;

        var limit = options.Value.MaxRequestBytes;
        if (http.Request.ContentLength > limit)
            return Problem(413, "request_too_large", $"Request body exceeds {limit} bytes.");

        ExtractRequest? request = null;
        try
        {
            await using var bounded = new LimitedReadStream(http.Request.Body, limit);
            request = await JsonSerializer.DeserializeAsync<ExtractRequest>(bounded, JsonSerializerOptions.Web, cancellationToken: ct);
        }
        catch (JsonException) { return Problem(400, "invalid_json", "Body JSON tidak valid."); }
        catch (ResponseTooLargeException) { return Problem(413, "request_too_large", $"Request body exceeds {limit} bytes."); }

        if (request?.Urls is not { Count: > 0 }) return Problem(400, "invalid_urls", "Daftar URL ekstraksi wajib diisi.");
        if (request.Urls.Count > MaxExtractUrls)
            return Problem(400, "too_many_urls", $"Maksimal {MaxExtractUrls} URL per permintaan ekstraksi.");
        foreach (var url in request.Urls)
        {
            // Validasi statis alamat publik; handler keluar tetap me-resolve ulang alamat sebelum socket dibuka.
            if (!IsPublicHttpUrl(url, outbound))
                return Problem(400, "invalid_url", "URL ekstraksi harus http/https absolut dan bukan alamat privat.");
        }

        try
        {
            return Results.Ok(await hub.ExtractAsync(request, ct));
        }
        catch (GatewayException ex)
        {
            return GatewayError(http, ex);
        }
    }

    /// <summary>
    /// Batas per menit data plane pencarian memakai limiter yang sama dengan jalur chat: kebijakan
    /// tenant/project/key, ditambah default <see cref="WebSearchOptions.RequestsPerMinute"/> pada scope tenant
    /// (yang terkecil menang, tanpa jendela hitungan kedua).
    /// </summary>
    private static async Task<IResult?> SearchRateLimitAsync(
        HttpContext http, GatewayCaller caller, PolicyEngine policy, RequestRateLimiter limiter,
        IOptions<WebSearchOptions> options, CancellationToken ct)
    {
        var policies = await policy.ActivePoliciesAsync(caller, ct);
        var defaultLimit = options.Value.RequestsPerMinute;
        var checks = new List<(string Scope, long Id, int Limit)>();
        foreach (var (scope, id) in new[]
                 {
                     (PolicyScopes.Tenant, caller.TenantId), (PolicyScopes.Project, caller.ProjectId),
                     (PolicyScopes.Key, caller.Key.Id),
                 })
        {
            policies.TryGetValue(scope, out var p);
            var limit = p?.RequestsPerMinute;
            if (scope == PolicyScopes.Tenant)
                limit = limit is { } own ? Math.Min(own, defaultLimit) : defaultLimit;
            if (limit is { } value) checks.Add((scope, id, value));
        }

        if (limiter.TryAcquire(checks, out var retryAfter)) return null;

        http.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);
        return Problem(429, "rate_limit_exceeded", "Terlalu banyak permintaan pencarian; coba lagi nanti.");
    }

    private static bool IsPublicHttpUrl(string? raw, OutboundSecurityPolicy outbound)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxUrlLength) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length > 0 || uri.Host.Length == 0) return false;
        if (uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)) return true;
        return IPAddress.TryParse(uri.DnsSafeHost, out var ip) && outbound.IsAddressAllowed(ip);
    }

    private static IResult GatewayError(HttpContext http, GatewayException ex)
    {
        if (ex.Data["RetryAfter"] is string retry) http.Response.Headers.RetryAfter = retry;
        return Results.Json(new { error = new
        {
            message = ex.Message, type = "gateway_error", param = (string?)null, code = ex.Code,
        } }, statusCode: ex.Status);
    }

    private static IResult Problem(int status, string code, string message) => Results.Json(new
    {
        error = new
        {
            message,
            type = status == 429 ? "rate_limit_error" : "invalid_request_error",
            param = (string?)null,
            code,
        }
    }, statusCode: status);


    /// <summary>Batas output efektif: terkecil dari batas model dan batas kebijakan yang terpasang.</summary>
    private static int? EffectiveLimit(int? modelLimit, int? policyLimit) =>
        modelLimit is { } m && policyLimit is { } p ? Math.Min(m, p) : modelLimit ?? policyLimit;

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
        if (response.StreamBody is { } stream) return new SseResult(stream);
        if (response.RetryAfter is not null) http.Response.Headers.RetryAfter = response.RetryAfter;
        return Results.Text(response.Body, "application/json", Encoding.UTF8, response.Status);
    }

    /// <summary>
    /// Respons SSE: header dikirim begitu event pertama upstream sudah siap, lalu body dipompa sampai selesai.
    /// <c>X-Accel-Buffering: no</c> menyuruh nginx tidak menahan chunk, dan <c>DisableBuffering</c> mematikan buffering server. Body selalu dijalankan, juga bila klien sudah
    /// pergi sebelum header terkirim: di dalamnya stream upstream dibuang, pemakaian dicatat, dan slot dilepas.
    /// </summary>
    private sealed class SseResult(Func<Stream, CancellationToken, Task> body) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            var response = http.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";
            // GA-06: matikan buffering respons milik server (mis. IIS in-process) sebelum header dikirim, supaya tiap
            // flush di batas event benar-benar sampai ke klien/proxy. Perlindungan klien lambat tetap ada: write dan
            // flush berbatas deadline total (GA-02).
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            // finally: body memegang pelepasan slot konkurensi dan dispose stream upstream, jadi harus jalan juga bila
            // StartAsync gagal dengan exception selain pembatalan (mis. IOException saat klien putus).
            try { await response.StartAsync(http.RequestAborted); }
            catch (OperationCanceledException) { /* klien pergi: body di bawah mencatatnya sebagai 499 */ }
            finally { await body(response.Body, http.RequestAborted); }
        }
    }
}
