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
            if (entry.Attempts == 0) entry.UsageIncomplete = false; // tidak ada pemakaian upstream.
            await recorder.RecordAsync(entry);
            // Opt-in (projects.log_content): isi request/response disimpan terpisah dengan masa simpan sendiri.
            // Gambar inline (data URL base64) tidak pernah ikut tersimpan: diganti penanda lebih dulu.
            await bodyStore.RecordAsync(entry, payload is JsonObject requestBody ? RequestBodyForLog(requestBody) : null, response.Body);
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

        var targets = await ForwardingTargets.LoadAsync(db, [model.Id], ct);
        // GA-08: route berfamily yang tidak bisa diteruskan dilewati (bukan dikirim dengan protokol chat yang salah).
        var sawUnsupportedFamily = false;

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
            foreach (var route in RouteSelector.Order(targets.Routes, Random.Shared))
            {
                if (!targets.Providers.TryGetValue(route.ProviderId, out var provider)
                    || !targets.Credentials.TryGetValue(route.ProviderId, out var credential))
                    continue;

                var family = targets.FamilyOf(route);
                if (targets.HasUnsupportedFamily(route))
                {
                    sawUnsupportedFamily = true;
                    log.LogWarning("Route {RouteId} (provider {ProviderId}, model upstream {UpstreamModel}) memakai family '{Family}' yang tidak bisa diteruskan gateway; dilewati.",
                        route.Id, provider.Id, route.UpstreamModel, family);
                    // ??=: kegagalan nyata dari route sebelumnya (mis. 429 + Retry-After) tidak boleh ditimpa route yang dilewati.
                    last ??= Failure(502, "unsupported_upstream_family",
                        "The provider route uses an API family the gateway cannot forward.", retriable: true);
                    continue;
                }

                entry.Attempts++;
                entry.ProviderId = provider.Id;
                // ponytail: percobaan yang gagal lalu ditinggalkan tidak dicatat sendiri, jadi biaya sisi provider
                // untuk percobaan itu tidak terlihat di usage_logs/usage_daily (plafon: satu baris per permintaan;
                // upgrade: baris per percobaan).
                var adapter = family is null ? null : FamilyAdapters.For(family);
                var attempt = await TryRouteAsync(caller, provider, credential, route, body, streaming, adapter, model.MaxOutputTokens, sw, ct);
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
            // Cache-write dibawa terpisah (adaptor Anthropic) supaya ditagih dengan harga tulis, bukan harga input biasa.
            var cacheWrite = JsonAccess.TokenCount(Get(Get(usage, "prompt_tokens_details"), "cache_creation_tokens")) ?? 0;
            entry.Cost = Pricing.Cost(price, entry.InputTokens, entry.CachedTokens, entry.OutputTokens, cacheWrite);
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
                    outcome = new StreamOutcome(StreamEnd.Interrupted,
                        (stream.Reader as ChatChunkReader)?.UsageSnapshot, null, "", true);
                }
                Bill(outcome.Usage);
                entry.UsageIncomplete |= outcome.End != StreamEnd.Completed || outcome.UsageIncomplete;
                if (entry.UsageIncomplete)
                    log.LogWarning("Usage stream tidak lengkap (request {RequestId}, provider {ProviderId}); token yang diketahui dipertahankan, biaya tercatat bukan tagihan final.",
                        entry.RequestId, entry.ProviderId);
                entry.FinishReason = outcome.FinishReason;
                var summary = JsonSerializer.Serialize(new
                {
                    streamed = true, content = outcome.Content, finish_reason = outcome.FinishReason, usage = outcome.Usage,
                    usage_incomplete = entry.UsageIncomplete,
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
                            StreamEnd.UpstreamError => "upstream_stream_error",
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
            Bill(result["usage"]);
            if (entry.UsageIncomplete)
                log.LogWarning("Usage respons tidak lengkap (request {RequestId}, provider {ProviderId}); token yang diketahui dipertahankan, biaya tercatat bukan tagihan final.",
                    entry.RequestId, entry.ProviderId);
            if (result["choices"] is JsonArray { Count: > 0 } choices) entry.FinishReason = Str(Get(choices[0], "finish_reason"));
            result["model"] = alias;
            return await Finish(new GatewayResponse(200, result.ToJsonString()), UsageStatuses.Ok);
        }

        if (last is null)
            return await Finish(
                GatewayResponse.Error(503, ErrorTypes.Server, "model_unavailable",
                    $"The model '{alias}' has no available provider."),
                UsageStatuses.Error, "no_available_provider");

        // Tidak satu pun route yang dicoba karena semuanya berfamily tidak didukung: alasannya sendiri di log.
        return await Finish(new GatewayResponse(last.Status, last.Body, last.RetryAfter), UsageStatuses.Error,
            sawUnsupportedFamily && entry.Attempts == 0 ? "unsupported_upstream_family" : "upstream_failed");
    }

    private async Task<UpstreamResult> TryRouteAsync(
        GatewayCaller caller, Provider provider, ProviderCredential credential, ModelRoute route,
        JsonObject body, bool streaming, IFamilyAdapter? adapter, int? modelMaxOutput, Stopwatch sw, CancellationToken ct)
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
        try { apiKey = protector.Unprotect(caller.TenantId, credential.ApiKeyEncrypted); }
        catch (CryptographicException ex)
        {
            log.LogError(ex, "Key provider {ProviderId} tidak bisa didekripsi", provider.Id);
            return Failure(502, "provider_credential_unreadable", "The provider credential could not be read.", retriable: true);
        }

        JsonObject upstreamBody;
        Uri uri;
        if (adapter is not null)
        {
            if (!adapter.TryToRequest(body, route.UpstreamModel, streaming, modelMaxOutput, out upstreamBody, out var unsupported))
                return new UpstreamResult(false, 400,
                    GatewayResponse.Error(400, ErrorTypes.InvalidRequest, "unsupported_request", unsupported).Body, false, null, null);
            uri = adapter.RequestUri(baseUri);
        }
        else
        {
            upstreamBody = (JsonObject)body.DeepClone();
            upstreamBody["model"] = route.UpstreamModel;
            if (streaming) ForceStreamUsage(upstreamBody);
            uri = new Uri(new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"), "chat/completions");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(upstreamBody.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(provider.AuthHeader, provider.AuthPrefix + apiKey);
        adapter?.AddHeaders(request);
        if (baseUri.Host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase)
            && baseUri.AbsolutePath.StartsWith("/inference/go/", StringComparison.Ordinal))
        {
            if (caller.SessionId is not null) request.Headers.Add("x-opencode-session", caller.SessionId);
            if (caller.UserAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", caller.UserAgent);
        }

        // Batas satu percobaan route: headers, event pertama sebelum commit, dan body REST (sukses maupun error)
        // berbagi satu deadline. HttpClient.Timeout selesai begitu SendAsync mengembalikan headers
        // (ResponseHeadersRead), jadi tanpa ini body yang menggantung menahan slot tenant tanpa batas.
        // Token hanya dipakai sampai hasil dikembalikan; stream yang sudah diserahkan diteruskan SsePump dengan
        // token pembatalan dan batas diam/totalnya sendiri (dispose di bawah menghentikan timer deadline).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.UpstreamTimeoutSeconds));
        var opCt = deadline.Token;

        var maxResponseBytes = options.Value.MaxResponseBytes;
        var started = sw.ElapsedMilliseconds;
        HttpResponseMessage? response = null;
        int? upstreamStatus = null;
        int? ttfbMs = null;
        var keep = false; // respons stream yang diteruskan dibuang pemiliknya (OpenStream), bukan di sini
        try
        {
            response = await httpFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, opCt);
            var ttfb = (int)(sw.ElapsedMilliseconds - started);
            var status = (int)response.StatusCode;
            upstreamStatus = status;
            ttfbMs = ttfb;
            var retryAfter = response.Headers.RetryAfter?.ToString();

            if (streaming && response.IsSuccessStatusCode
                && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var (open, failure) = await OpenStreamAsync(response, status, ttfb, adapter, opCt);
                if (failure is not null) return failure;
                keep = true;
                return new UpstreamResult(true, status, "", false, status, (int)(sw.ElapsedMilliseconds - started), Stream: open);
            }

            // GA-07: klien meminta SSE tetapi upstream menjawab sukses dengan bukan event-stream (JSON biasa karena
            // stream:true diabaikan, NDJSON, atau tanpa Content-Type). Itu ketidakcocokan protokol: ditolak SEBELUM ada
            // yang dikirim ke klien (route berikutnya masih bisa dicoba), bukan diturunkan diam-diam menjadi respons REST.
            if (streaming && response.IsSuccessStatusCode)
                return Failure(502, "upstream_protocol_mismatch", "The provider did not return an event stream for a streaming request.",
                    retriable: true, upstreamStatus: status, ttfb: ttfb);

            var text = await BoundedContent.ReadStringAsync(response.Content, maxResponseBytes, opCt);

            if (response.IsSuccessStatusCode)
            {
                var parsed = ParseObject(text);
                if (adapter is not null && parsed is not null) parsed = adapter.ToChatCompletion(parsed);
                if (parsed is not null)
                    return new UpstreamResult(true, status, adapter is not null ? parsed.ToJsonString() : text, false, status, ttfb);
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
            // Deadline percobaan, bukan klien: status dan TTFB yang sudah diketahui dipertahankan supaya log
            // membedakan headers yang tak pernah datang dari body yang menggantung setelah headers.
            return Failure(504, "upstream_timeout", "The provider did not respond in time.", retriable: true,
                upstreamStatus: upstreamStatus, ttfb: ttfbMs);
        }
        catch (ResponseTooLargeException)
        {
            log.LogWarning("Respons provider {ProviderId} melebihi {MaxBytes} byte; dihentikan.", provider.Id, maxResponseBytes);
            return Failure(502, "upstream_response_too_large", "The provider returned a response that is too large.", retriable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            // Sebagian jalur transport melaporkan koneksi yang terputus saat deadline sebagai IOException, bukan
            // OperationCanceledException; deadline tetap timeout, bukan upstream yang tidak bisa dihubungi.
            if (opCt.IsCancellationRequested && !ct.IsCancellationRequested)
                return Failure(504, "upstream_timeout", "The provider did not respond in time.", retriable: true,
                    upstreamStatus: upstreamStatus, ttfb: ttfbMs);
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
        HttpResponseMessage response, int status, int ttfb, IFamilyAdapter? adapter, CancellationToken ct)
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
        if (adapter is not null)
        {
            // Event pertama diterjemahkan sekarang; sisanya dibaca SsePump lewat penerjemah yang sama.
            var translator = adapter.TranslateStream(reader);
            foreach (var raw in lines) translator.Feed(raw);
            return (new OpenStream(response, translator, translator.Drain()), null);
        }
        return (new OpenStream(response, reader, lines), null);
    }

    private static bool FirstEventIsError(List<string> lines)
    {
        // Data event pertama bisa terpecah di beberapa baris data: (spesifikasi SSE menggabungkannya dengan \n).
        var payloads = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && payloads.Count > 0) break;
            if (SsePump.TryGetData(line, out var p)) payloads.Add(p);
        }
        if (payloads.Count == 0) return false;
        if (payloads[0].Trim() == "[DONE]") return false;
        // Gabungan valid dipakai bila ada; selain itu perilaku lama: hanya baris data pertama.
        var first = (payloads.Count > 1 ? ParseObject(string.Join('\n', payloads)) : null) ?? ParseObject(payloads[0]);
        // Chat/Anthropic: objek "error"; Responses: event type "error" atau "response.failed".
        var type = first?["type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
        return first?["error"] is JsonObject || type is "error" or "response.failed";
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
        var input = JsonAccess.TokenCount(Get(usage, "prompt_tokens"));
        var output = JsonAccess.TokenCount(Get(usage, "completion_tokens"));
        entry.UsageIncomplete = input is null || output is null;
        entry.InputTokens = input ?? 0;
        entry.OutputTokens = output ?? 0;
        entry.CachedTokens = JsonAccess.TokenCount(Get(Get(usage, "prompt_tokens_details"), "cached_tokens")) ?? 0;
        entry.ReasoningTokens = JsonAccess.TokenCount(Get(Get(usage, "completion_tokens_details"), "reasoning_tokens")) ?? 0;
    }

    /// <summary>
    /// Body permintaan untuk <c>projects.log_content</c>: isi data URL gambar diganti penanda supaya byte gambar
    /// tidak pernah masuk database, walau pencatatan isi dinyalakan. Body tanpa gambar tidak disalin.
    /// </summary>
    private static string RequestBodyForLog(JsonObject body)
    {
        if (!ContainsImageData(body)) return body.ToJsonString();
        var redacted = (JsonObject)body.DeepClone();
        RedactImageData(redacted);
        return redacted.ToJsonString();
    }

    /// <summary>Pemeriksaan murah lebih dulu supaya salinan body multimodal besar tidak dibuat tiap permintaan.</summary>
    private static bool ContainsImageData(JsonNode? node) => node switch
    {
        JsonObject o => o.Any(pair => ImageDataUrl(pair.Value) is not null) || o.Any(pair => ContainsImageData(pair.Value)),
        JsonArray a => a.Any(ContainsImageData),
        _ => false,
    };

    /// <summary>Ganti isi <c>image_url</c> berbentuk data URL dengan penanda; struktur lain dibiarkan untuk debug.</summary>
    private static void RedactImageData(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (ImageDataUrl(o["image_url"]) is not null) o["image_url"] = "[inline image omitted]";
            else if (o["image_url"] is JsonObject image && ImageDataUrl(image["url"]) is not null) image["url"] = "[inline image omitted]";
            foreach (var pair in o.ToList()) RedactImageData(pair.Value);
        }
        else if (node is JsonArray a)
        {
            foreach (var item in a) RedactImageData(item);
        }
    }

    /// <summary>URL data URL gambar (<c>data:image/...</c>) atau null.</summary>
    private static string? ImageDataUrl(JsonNode? node) => Str(node) is { } url
        && url.StartsWith("data:image/", StringComparison.Ordinal) ? url : null;

    /// <summary>Indexer JsonNode melempar bila node bukan objek; upstream tidak dipercaya, jadi baca lewat sini.</summary>
    private static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
