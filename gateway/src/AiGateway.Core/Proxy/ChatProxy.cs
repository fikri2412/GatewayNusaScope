using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Maintenance;
using AiGateway.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Proxy;

/// <summary>Satu hasil percobaan ke satu route upstream.</summary>
internal sealed record UpstreamResult(
    bool Ok, int Status, string Body, bool Retriable, int? UpstreamStatus, int? TtfbMs, string? RetryAfter = null);

/// <summary>
/// Pipeline <c>/v1/chat/completions</c> untuk pemanggil yang sudah terautentikasi (tenant sudah diset di
/// <see cref="GatewayDbContext"/>): validasi, pemilihan model dan route, forwarding, fallback, pencatatan.
/// </summary>
public sealed class ChatProxy(
    GatewayDbContext db,
    IHttpClientFactory httpFactory,
    ProviderKeyProtector protector,
    UsageRecorder recorder,
    PolicyEngine policy,
    RequestBodyStore bodyStore,
    ILogger<ChatProxy> log)
{
    public const string HttpClientName = "upstream";
    private const int MaxEndUserLength = 200;

    public async Task<GatewayResponse> HandleAsync(GatewayCaller caller, JsonNode? payload, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var entry = new UsageLog
        {
            TenantId = caller.TenantId,
            ProjectId = caller.ProjectId,
            ApiKeyId = caller.Key.Id,
            RequestId = caller.RequestId,
            Status = UsageStatuses.Denied,
            ClientIp = caller.ClientIp,
            UserAgent = caller.UserAgent,
            Tags = caller.Tags,
        };

        async Task<GatewayResponse> Finish(GatewayResponse response, string status, string? reason = null)
        {
            entry.Status = status;
            entry.HttpStatus = response.Status;
            entry.DeniedReason = reason;
            entry.LatencyMs = (int)sw.ElapsedMilliseconds;
            await recorder.RecordAsync(entry);
            // Opt-in (projects.log_content): isi request/response disimpan terpisah dengan masa simpan sendiri.
            await bodyStore.RecordAsync(entry, payload is JsonObject requestBody ? requestBody.ToJsonString() : null, response.Body);
            return response;
        }

        Task<GatewayResponse> Reject(int status, string type, string code, string message) =>
            Finish(GatewayResponse.Error(status, type, code, message), UsageStatuses.Denied, code);

        if (payload is not JsonObject body)
            return await Reject(400, ErrorTypes.InvalidRequest, "invalid_json", "Request body must be a JSON object.");
        if (Str(body["model"]) is not { Length: > 0 } alias)
            return await Reject(400, ErrorTypes.InvalidRequest, "missing_model", "'model' is required.");
        if (body["messages"] is not JsonArray { Count: > 0 })
            return await Reject(400, ErrorTypes.InvalidRequest, "missing_messages", "'messages' must be a non-empty array.");
        if (body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var stream) && stream)
            return await Reject(400, ErrorTypes.InvalidRequest, "streaming_not_supported", "Streaming is not supported yet; send 'stream': false.");

        if (Str(body["user"]) is { } endUser)
            entry.EndUser = endUser.Length <= MaxEndUserLength ? endUser : endUser[..MaxEndUserLength];

        var model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Alias == alias && m.Enabled, ct);
        if (model is null)
            return await Reject(404, ErrorTypes.NotFound, "model_not_found",
                $"The model '{alias}' does not exist or you do not have access to it.");
        entry.ModelId = model.Id;

        var decision = await policy.EvaluateAsync(caller, alias, ct);
        if (decision.Denial is { } denial)
            return await Finish(denial.ToResponse(), UsageStatuses.Denied, denial.Reason);

        var routes = await db.ModelRoutes.AsNoTracking().Where(r => r.ModelId == model.Id && r.Enabled).ToListAsync(ct);
        var providerIds = routes.Select(r => r.ProviderId).Distinct().ToList();
        var providers = await db.Providers.AsNoTracking().Where(p => providerIds.Contains(p.Id) && p.Enabled)
            .ToDictionaryAsync(p => p.Id, ct);
        var credentials = await db.ProviderCredentials.AsNoTracking()
            .Where(c => providerIds.Contains(c.ProviderId) && c.Status == CredentialStatuses.Active)
            .ToDictionaryAsync(c => c.ProviderId, ct);

        var now = DateTime.UtcNow;
        var prices = await db.ModelPrices.AsNoTracking()
            .Where(p => p.ModelId == model.Id && p.EffectiveFrom <= now).ToListAsync(ct);

        LimitOutputTokens(body, model.MaxOutputTokens, decision.MaxTokens);

        UpstreamResult? last = null;
        JsonNode? success = null;
        try
        {
            foreach (var route in RouteSelector.Order(routes, Random.Shared))
            {
                if (!providers.TryGetValue(route.ProviderId, out var provider)
                    || !credentials.TryGetValue(route.ProviderId, out var credential))
                    continue;

                entry.Attempts++;
                entry.ProviderId = provider.Id;
                var attempt = await TryRouteAsync(caller.TenantId, provider, credential, route, body, sw, ct);
                entry.UpstreamStatus = attempt.UpstreamStatus;
                entry.TtfbMs = attempt.TtfbMs;
                last = attempt;
                if (attempt.Ok)
                {
                    success = JsonNode.Parse(attempt.Body);
                    break;
                }
                if (!attempt.Retriable) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Klien memutus koneksi; upstream mungkin sudah berjalan, jadi tetap dicatat.
            await Finish(GatewayResponse.Error(499, ErrorTypes.Server, "client_closed_request", "Client closed the request."),
                UsageStatuses.Error);
            throw;
        }

        entry.FallbackUsed = success is not null && entry.Attempts > 1;

        if (success is JsonObject result)
        {
            ReadUsage(result, entry);
            var price = Pricing.Pick(prices, now, entry.InputTokens);
            if (price is not null)
            {
                entry.Cost = Pricing.Cost(price, entry.InputTokens, entry.CachedTokens, entry.OutputTokens);
                entry.InputPriceUsed = price.InputPricePer1M;
                entry.OutputPriceUsed = price.OutputPricePer1M;
                entry.Currency = price.Currency;
            }
            result["model"] = alias;
            return await Finish(new GatewayResponse(200, result.ToJsonString()), UsageStatuses.Ok);
        }

        if (last is null)
            return await Finish(
                GatewayResponse.Error(503, ErrorTypes.Server, "model_unavailable",
                    $"The model '{alias}' has no available provider."),
                UsageStatuses.Error, "no_available_provider");

        return await Finish(new GatewayResponse(last.Status, last.Body, last.RetryAfter), UsageStatuses.Error, "upstream_failed");
    }

    private async Task<UpstreamResult> TryRouteAsync(
        long tenantId, Provider provider, ProviderCredential credential, ModelRoute route,
        JsonObject body, Stopwatch sw, CancellationToken ct)
    {
        string apiKey;
        try { apiKey = protector.Unprotect(tenantId, credential.ApiKeyEncrypted); }
        catch (CryptographicException ex)
        {
            log.LogError(ex, "Key provider {ProviderId} tidak bisa didekripsi", provider.Id);
            return Failure(502, "provider_credential_unreadable", "The provider credential could not be read.", retriable: true);
        }

        var upstreamBody = (JsonObject)body.DeepClone();
        upstreamBody["model"] = route.UpstreamModel;

        var uri = new Uri(new Uri(provider.BaseUrl.TrimEnd('/') + "/"), "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(upstreamBody.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(provider.AuthHeader, provider.AuthPrefix + apiKey);

        var started = sw.ElapsedMilliseconds;
        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var ttfb = (int)(sw.ElapsedMilliseconds - started);
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            var retryAfter = response.Headers.RetryAfter?.ToString();

            if (response.IsSuccessStatusCode)
            {
                if (TryParseObject(text))
                    return new UpstreamResult(true, status, text, false, status, ttfb);
                return Failure(502, "invalid_upstream_response", "The provider returned an invalid response.",
                    retriable: true, upstreamStatus: status, ttfb: ttfb);
            }

            // 401/403: key provider salah (masalah tenant, bukan klien); 404: model upstream tidak ada.
            // Semuanya bisa teratasi oleh route lain, jadi dicoba route berikutnya.
            if (status is 401 or 403)
                return Failure(502, "provider_auth_failed", "The provider rejected the configured credentials.",
                    retriable: true, upstreamStatus: status, ttfb: ttfb);
            if (status == 429)
                return new UpstreamResult(false, 429,
                    GatewayResponse.Error(429, ErrorTypes.RateLimit, "upstream_rate_limited", "The provider is rate limiting requests.").Body,
                    true, status, ttfb, retryAfter);
            if (status is 404 or 408 or >= 500)
                return Failure(502, "upstream_error", $"The provider returned an error (HTTP {status}).",
                    retriable: true, upstreamStatus: status, ttfb: ttfb);

            // Kesalahan permintaan lain (400, 422, ...) diteruskan apa adanya; route lain tidak akan menolong.
            return new UpstreamResult(false, status, text, false, status, ttfb);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(504, "upstream_timeout", "The provider did not respond in time.", retriable: true);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Provider {ProviderId} tidak bisa dihubungi", provider.Id);
            return Failure(502, "upstream_unreachable", "The provider could not be reached.", retriable: true);
        }
    }

    private static UpstreamResult Failure(int status, string code, string message, bool retriable, int? upstreamStatus = null, int? ttfb = null) =>
        new(false, status, GatewayResponse.Error(status, ErrorTypes.Upstream, code, message).Body, retriable, upstreamStatus, ttfb);

    private static bool TryParseObject(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>
    /// Terapkan batas output terkecil dari model dan kebijakan: nilai klien yang lebih besar dipotong. Bila klien tidak
    /// mengirim batas dan ada kebijakan, batas itu dipaksakan; batas model saja tidak menambah field.
    /// </summary>
    private static void LimitOutputTokens(JsonObject body, int? modelLimit, int? policyLimit)
    {
        if (modelLimit is null && policyLimit is null) return;
        var limit = Math.Min(modelLimit ?? int.MaxValue, policyLimit ?? int.MaxValue);
        var sent = false;
        foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
            if (body[name] is JsonValue v && v.TryGetValue<long>(out var n))
            {
                sent = true;
                if (n > limit) body[name] = limit;
            }
        if (!sent && policyLimit is not null) body["max_tokens"] = limit;
    }

    private static void ReadUsage(JsonObject response, UsageLog entry)
    {
        var usage = response["usage"];
        entry.InputTokens = Int(usage?["prompt_tokens"]);
        entry.OutputTokens = Int(usage?["completion_tokens"]);
        entry.CachedTokens = Int(usage?["prompt_tokens_details"]?["cached_tokens"]);
        entry.ReasoningTokens = Int(usage?["completion_tokens_details"]?["reasoning_tokens"]);
        if (response["choices"] is JsonArray { Count: > 0 } choices)
            entry.FinishReason = Str(choices[0]?["finish_reason"]);
    }

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
