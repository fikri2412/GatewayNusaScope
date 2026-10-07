using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Maintenance;
using AiGateway.Core.Options;
using AiGateway.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Proxy;

/// <summary>Satu hasil percobaan ke satu route upstream.</summary>
internal sealed record UpstreamResult(
    bool Ok, int Status, string Body, bool Retriable, int? UpstreamStatus, int? TtfbMs, string? RetryAfter = null,
    OpenStream? Stream = null);

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
    OutboundSecurityPolicy outbound,
    TenantConcurrencyGate concurrency,
    TimeProvider clock,
    IOptions<ProxyOptions> options,
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
        // stream: absen/null/false → respons utuh; true → SSE; bentuk lain ditolak (OpenAI juga menjawab 400).
        var streaming = false;
        if (body["stream"] is { } streamValue
            && (streamValue is not JsonValue streamJson || !streamJson.TryGetValue<bool>(out streaming)))
            return await Reject(400, ErrorTypes.InvalidRequest, "invalid_stream", "'stream' must be a boolean.");
        // Chunk usage-saja hanya diteruskan ke klien yang memintanya; gateway sendiri selalu memintanya ke upstream.
        var wantsUsage = streaming && body["stream_options"] is JsonObject streamOptions
            && streamOptions["include_usage"] is JsonValue includeUsage && includeUsage.TryGetValue<bool>(out var include) && include;

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

        var now = clock.GetUtcNow().UtcDateTime;
        var prices = await db.ModelPrices.AsNoTracking()
            .Where(p => p.ModelId == model.Id && p.EffectiveFrom <= now).ToListAsync(ct);

        LimitOutputTokens(body, model.MaxOutputTokens, decision.MaxTokens);

        // Batas permintaan bersamaan per tenant (GATEWAY.md bagian 8) diperiksa paling akhir, setelah semua
        // kebijakan lolos, dan hanya permintaan yang benar-benar diteruskan ke upstream yang memakai slot.
        if (!concurrency.TryAcquire(caller.TenantId, options.Value.MaxConcurrentPerTenant))
            return await Finish(
                GatewayResponse.Error(429, ErrorTypes.RateLimit, "concurrency_limit_exceeded",
                    "Too many requests are already in flight for this tenant; retry shortly.", "1"),
                UsageStatuses.Denied, "concurrency_limit_exceeded");

        UpstreamResult? last = null;
        JsonNode? success = null;
        OpenStream? open = null;
        try
        {
            foreach (var route in RouteSelector.Order(routes, Random.Shared))
            {
                if (!providers.TryGetValue(route.ProviderId, out var provider)
                    || !credentials.TryGetValue(route.ProviderId, out var credential))
                    continue;

                entry.Attempts++;
                entry.ProviderId = provider.Id;
                // ponytail: percobaan yang gagal lalu ditinggalkan tidak dicatat sendiri, jadi biaya sisi provider
                // untuk percobaan itu tidak terlihat di usage_logs/usage_daily (plafon: satu baris per permintaan;
                // upgrade: baris per percobaan).
                var attempt = await TryRouteAsync(caller.TenantId, provider, credential, route, body, streaming, sw, ct);
                entry.UpstreamStatus = attempt.UpstreamStatus;
                entry.TtfbMs = attempt.TtfbMs;
                last = attempt;
                if (attempt.Ok)
                {
                    // Stream yang terbuka sudah lolos event pertama: dari sini tidak ada fallback lagi.
                    open = attempt.Stream;
                    if (open is null) success = JsonNode.Parse(attempt.Body);
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
        finally
        {
            // Stream yang terbuka memegang slot sampai pompanya selesai (RunStream).
            if (open is null) concurrency.Release(caller.TenantId);
        }

        entry.FallbackUsed = (success is not null || open is not null) && entry.Attempts > 1;

        // Respons utuh dan akhir stream sama-sama berujung di sini: token dibaca, harga dipilih, biaya dihitung.
        void Bill(JsonNode? usage)
        {
            ReadUsage(usage, entry);
            var price = Pricing.Pick(prices, now, entry.InputTokens);
            if (price is null) return;
            entry.Cost = Pricing.Cost(price, entry.InputTokens, entry.CachedTokens, entry.OutputTokens);
            entry.InputPriceUsed = price.InputPricePer1M;
            entry.OutputPriceUsed = price.OutputPricePer1M;
            entry.Currency = price.Currency;
        }

        // Dijalankan endpoint setelah header SSE terkirim. Pencatatan memakai Finish (tanpa token request) supaya
        // pemakaian tetap tercatat walau klien putus di tengah stream; slot konkurensi dilepas di sini.
        async Task RunStream(Stream output, CancellationToken outCt)
        {
            var stream = open!;
            try
            {
                var o = options.Value;
                StreamOutcome outcome;
                try
                {
                    outcome = await SsePump.RunAsync(stream, output, alias, wantsUsage,
                        TimeSpan.FromSeconds(o.UpstreamTimeoutSeconds), TimeSpan.FromSeconds(o.MaxStreamSeconds), outCt);
                }
                catch (Exception ex)
                {
                    // Bug di pompa tidak boleh membuat pemakaian tidak tercatat atau slot bocor.
                    log.LogError(ex, "Pompa SSE gagal (request {RequestId}, provider {ProviderId})", entry.RequestId, entry.ProviderId);
                    outcome = new StreamOutcome(StreamEnd.Interrupted, null, null, "");
                }
                if (outcome.Usage is null && outcome.End == StreamEnd.Completed)
                    // ponytail: sama dengan respons utuh: provider yang tidak mengirim usage dicatat 0 token/0 biaya.
                    log.LogWarning("Stream selesai tanpa usage (request {RequestId}, provider {ProviderId}); token dan biaya dicatat 0.",
                        entry.RequestId, entry.ProviderId);
                Bill(outcome.Usage);
                entry.FinishReason = outcome.FinishReason;
                var summary = JsonSerializer.Serialize(new
                {
                    streamed = true, content = outcome.Content, finish_reason = outcome.FinishReason, usage = outcome.Usage,
                });
                switch (outcome.End)
                {
                    case StreamEnd.Completed:
                        await Finish(new GatewayResponse(200, summary), UsageStatuses.Ok);
                        break;
                    case StreamEnd.ClientClosed:
                        await Finish(new GatewayResponse(499, summary), UsageStatuses.Error); // body = ringkasan parsial untuk log_content
                        break;
                    default:
                        // Status 200 sudah terkirim ke klien; kegagalan di tengah stream hanya terlihat di usage_logs.
                        await Finish(new GatewayResponse(200, summary), UsageStatuses.Error, outcome.End switch
                        {
                            StreamEnd.Timeout => "upstream_stream_timeout",
                            StreamEnd.TooLarge => "upstream_response_too_large",
                            _ => "upstream_stream_interrupted",
                        });
                        break;
                }
            }
            finally
            {
                stream.Dispose();
                concurrency.Release(caller.TenantId);
            }
        }

        if (open is not null)
            return new GatewayResponse(200, "", StreamBody: RunStream);

        if (success is JsonObject result)
        {
            if (result["usage"] is not JsonObject)
                // ponytail: provider yang tidak melaporkan usage dicatat 0 token/0 biaya (baris ok) supaya
                // permintaannya tidak hilang; plafon: kuota dan laporan tidak terhitung; upgrade: estimasi token
                // dari respons atau tolak provider yang tidak mengirim usage.
                log.LogWarning("Respons 200 tanpa usage (request {RequestId}, provider {ProviderId}); token dan biaya dicatat 0.",
                    entry.RequestId, entry.ProviderId);
            Bill(result["usage"]);
            if (result["choices"] is JsonArray { Count: > 0 } choices) entry.FinishReason = Str(Get(choices[0], "finish_reason"));
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
        JsonObject body, bool streaming, Stopwatch sw, CancellationToken ct)
    {
        // Validasi ulang tepat sebelum dipakai: baris provider bisa berubah tanpa lewat provisioning (restore/seed).
        // Gagal di sini berarti permintaan tidak dikirim sama sekali, jadi key provider tidak pernah keluar.
        Uri baseUri;
        try { baseUri = outbound.RequireHttpsUri(provider.BaseUrl, "provider_misconfigured"); }
        catch (GatewayException ex)
        {
            log.LogError("Base URL provider {ProviderId} tidak valid ({Code}); permintaan tidak dikirim.", provider.Id, ex.Code);
            return Failure(502, "provider_misconfigured", "The provider configuration is invalid.", retriable: true);
        }

        string apiKey;
        try { apiKey = protector.Unprotect(tenantId, credential.ApiKeyEncrypted); }
        catch (CryptographicException ex)
        {
            log.LogError(ex, "Key provider {ProviderId} tidak bisa didekripsi", provider.Id);
            return Failure(502, "provider_credential_unreadable", "The provider credential could not be read.", retriable: true);
        }

        var upstreamBody = (JsonObject)body.DeepClone();
        upstreamBody["model"] = route.UpstreamModel;
        if (streaming) ForceStreamUsage(upstreamBody);

        var uri = new Uri(new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"), "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(upstreamBody.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(provider.AuthHeader, provider.AuthPrefix + apiKey);

        var maxResponseBytes = options.Value.MaxResponseBytes;
        var started = sw.ElapsedMilliseconds;
        HttpResponseMessage? response = null;
        var keep = false; // respons stream yang diteruskan dibuang pemiliknya (OpenStream), bukan di sini
        try
        {
            response = await httpFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var ttfb = (int)(sw.ElapsedMilliseconds - started);
            var status = (int)response.StatusCode;
            var retryAfter = response.Headers.RetryAfter?.ToString();

            if (streaming && response.IsSuccessStatusCode
                && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var (open, failure) = await OpenStreamAsync(response, status, ttfb, ct);
                if (failure is not null) return failure;
                keep = true;
                return new UpstreamResult(true, status, "", false, status, (int)(sw.ElapsedMilliseconds - started), Stream: open);
            }

            // Upstream yang mengabaikan stream:true dan menjawab JSON biasa diperlakukan sebagai respons utuh.
            var text = await BoundedContent.ReadStringAsync(response.Content, maxResponseBytes, ct);

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

            // Kesalahan permintaan lain (400, 422, ...) diteruskan apa adanya hanya bila berbentuk JSON (format
            // error OpenAI); body lain (mis. halaman HTML dari WAF upstream) diganti error gateway dengan status
            // yang sama supaya klien tidak menerima HTML berlabel application/json. Route lain tidak akan menolong.
            return TryParseObject(text)
                ? new UpstreamResult(false, status, text, false, status, ttfb)
                : Failure(status, "upstream_error", $"The provider returned an error (HTTP {status}).",
                    retriable: false, upstreamStatus: status, ttfb: ttfb);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(504, "upstream_timeout", "The provider did not respond in time.", retriable: true);
        }
        catch (ResponseTooLargeException)
        {
            log.LogWarning("Respons provider {ProviderId} melebihi {MaxBytes} byte; dihentikan.", provider.Id, maxResponseBytes);
            return Failure(502, "upstream_response_too_large", "The provider returned a response that is too large.", retriable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            log.LogWarning(ex, "Provider {ProviderId} tidak bisa dihubungi", provider.Id);
            return Failure(502, "upstream_unreachable", "The provider could not be reached.", retriable: true);
        }
        finally
        {
            if (!keep) response?.Dispose();
        }
    }

    /// <summary>
    /// Baca event pertama stream sebelum apa pun dikirim ke klien: stream kosong atau event error pertama masih bisa
    /// diganti route berikutnya. Setelah ini respons sudah "committed" dan tidak ada fallback lagi.
    /// </summary>
    private async Task<(OpenStream? Stream, UpstreamResult? Failure)> OpenStreamAsync(
        HttpResponseMessage response, int status, int ttfb, CancellationToken ct)
    {
        var o = options.Value;
        var reader = new StreamReader(
            new LimitedReadStream(await response.Content.ReadAsStreamAsync(ct), o.MaxResponseBytes), Encoding.UTF8);
        var lines = new List<string>();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(o.UpstreamTimeoutSeconds));
        var sawData = false;
        while (await reader.ReadLineAsync(idle.Token) is { } line)
        {
            lines.Add(line);
            if (line.Length > 0) { sawData = true; continue; }
            if (sawData) break; // event pertama lengkap
        }

        if (!sawData || FirstEventIsError(lines))
            return (null, Failure(502, "upstream_error", "The provider returned an empty or failed stream.",
                retriable: true, upstreamStatus: status, ttfb: ttfb));
        return (new OpenStream(response, reader, lines), null);
    }

    private static bool FirstEventIsError(List<string> lines)
    {
        foreach (var line in lines)
            if (SsePump.TryGetData(line, out var payload))
                return payload.Trim() != "[DONE]" && ParseObject(payload)?["error"] is JsonObject;
        return false;
    }

    /// <summary>Penagihan stream bergantung pada chunk usage terakhir; minta upstream mengirimnya.</summary>
    private static void ForceStreamUsage(JsonObject upstreamBody)
    {
        if (upstreamBody["stream_options"] is not JsonObject streamOptions)
            upstreamBody["stream_options"] = streamOptions = new JsonObject();
        streamOptions["include_usage"] = true;
    }

    private static UpstreamResult Failure(int status, string code, string message, bool retriable, int? upstreamStatus = null, int? ttfb = null) =>
        new(false, status, GatewayResponse.Error(status, ErrorTypes.Upstream, code, message).Body, retriable, upstreamStatus, ttfb);

    private static bool TryParseObject(string text) => ParseObject(text) is not null;

    private static JsonObject? ParseObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Terapkan batas output terkecil dari model dan kebijakan. Field batas yang ada di body hanya dipertahankan
    /// bila berisi bilangan bulat 1..batas; nilai lain (string, pecahan, negatif, nol, di luar jangkauan long)
    /// ditimpa dengan batas supaya upstream tidak menafsirkannya sendiri. Bila klien tidak mengirim batas dan ada
    /// kebijakan, batas itu dipaksakan; batas model saja tidak menambah field.
    /// </summary>
    private static void LimitOutputTokens(JsonObject body, int? modelLimit, int? policyLimit)
    {
        if (modelLimit is null && policyLimit is null) return;
        var limit = Math.Min(modelLimit ?? int.MaxValue, policyLimit ?? int.MaxValue);
        var sent = false;
        foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
        {
            if (!body.ContainsKey(name)) continue;
            sent = true;
            body[name] = body[name] is JsonValue v && v.TryGetValue<long>(out var n) && n >= 1 && n <= limit ? n : limit;
        }
        if (!sent && policyLimit is not null) body["max_tokens"] = limit;
    }

    private static void ReadUsage(JsonNode? usage, UsageLog entry)
    {
        entry.InputTokens = Int(Get(usage, "prompt_tokens"));
        entry.OutputTokens = Int(Get(usage, "completion_tokens"));
        entry.CachedTokens = Int(Get(Get(usage, "prompt_tokens_details"), "cached_tokens"));
        entry.ReasoningTokens = Int(Get(Get(usage, "completion_tokens_details"), "reasoning_tokens"));
    }

    /// <summary>Indexer JsonNode melempar bila node bukan objek; upstream tidak dipercaya, jadi baca lewat sini.</summary>
    private static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
