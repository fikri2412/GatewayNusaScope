using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.ExceptionServices;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.WebSearch;

/// <summary>Hasil uji kunci penyedia dari panel admin.</summary>
public sealed record ProviderTestResult(bool Success, string Message);

/// <summary>
/// Pemilihan penyedia pencarian web: bobot acak, filter kapabilitas/anggaran, rollover periode kuota,
/// sirkuit dengan probe half-open satu-lease, failover tanpa mengulang penyedia, dan akuntansi kredit
/// atomik terhadap database (aman multi-proses).
/// </summary>
public sealed class WebSearchHub(
    GatewayDbContext db,
    ProviderKeyProtector protector,
    IHttpClientFactory clientFactory,
    IEnumerable<ISearchProvider> adapters,
    IRandomProvider random,
    TimeProvider clock,
    IOptions<WebSearchOptions> options,
    ILogger<WebSearchHub> log)
{
    private static readonly Meter Meter = new("AiGateway.WebSearch", "1.0");
    private static readonly Counter<long> RequestsCounter = Meter.CreateCounter<long>("gateway.websearch.requests");
    private static readonly Counter<long> SuccessCounter = Meter.CreateCounter<long>("gateway.websearch.success");
    private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>("gateway.websearch.failures");
    private static readonly Counter<long> FailoverCounter = Meter.CreateCounter<long>("gateway.websearch.failovers");

    private readonly WebSearchOptions _options = options.Value;
    private static readonly SearchRequest TestRequest = new("test", 1);
    private readonly Dictionary<string, ISearchProvider> _adapters = adapters
        .GroupBy(a => a.Type, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new GatewayException(StatusCodes.Status400BadRequest, "invalid_query", "Query pencarian wajib diisi.");
        if (request.Query.Length > 500)
            throw new GatewayException(StatusCodes.Status400BadRequest, "query_too_long", "Query maksimal 500 karakter.");

        return await ExecuteAsync("search", request, ct,
            (adapter, config, apiKey, client, token) => adapter.SearchAsync(config, apiKey, request, client, token),
            (adapter, config, req, response) => adapter.EstimateSearchCost(config, req, response.Results.Count),
            static (_, _, _, _) => 0m);
    }

    public async Task<ExtractResponse> ExtractAsync(ExtractRequest request, CancellationToken ct)
    {
        if (request.Urls == null || request.Urls.Count == 0)
            throw new GatewayException(StatusCodes.Status400BadRequest, "invalid_urls", "Daftar URL ekstraksi wajib diisi.");
        if (request.Urls.Count > 5)
            throw new GatewayException(StatusCodes.Status400BadRequest, "too_many_urls", "Maksimal 5 URL per permintaan ekstraksi.");

        foreach (var u in request.Urls)
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
                throw new GatewayException(StatusCodes.Status400BadRequest, "invalid_url_format", $"URL '{u}' tidak valid.");
        }

        return await ExecuteAsync("extract", request, ct,
            (adapter, config, apiKey, client, token) => adapter.ExtractAsync(config, apiKey, request, client, token),
            (adapter, config, req, response) => adapter.EstimateExtractCost(config, req, response.Pages.Count),
            static (adapter, config, req, ex) => ex is IPartialExtractAccounting { SuccessfulPageCount: > 0 } partial
                ? adapter.EstimateExtractCost(config, req, partial.SuccessfulPageCount)
                : 0m);
    }

    /// <summary>
    /// Uji kunci satu penyedia untuk admin: tanpa failover, kredit satu permintaan dasar, dan transisi
    /// status yang sama dengan jalur data plane (sukses memulihkan, gagal mencatat klasifikasi).
    /// </summary>
    public async Task<ProviderTestResult> TestAsync(Guid publicId, CancellationToken ct)
    {
        var config = await db.SearchProviderConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PublicId == publicId && c.DeletedAt == null, ct)
            ?? throw GatewayException.NotFound("Penyedia pencarian");
        if (!await db.SearchProviderStates.AsNoTracking().AnyAsync(s => s.ProviderId == config.Id, ct))
            await EnsureStateAsync(config, Now, ct);

        // Pasangan config+state terverifikasi supaya uji tidak memakai kunci basi setelah admin merotasi.
        if (await LoadFreshProviderAsync(config.Id, ct) is not { } pair)
            throw GatewayException.NotFound("Penyedia pencarian");
        config = pair.Config;
        var state = pair.State;
        if (!_adapters.TryGetValue(config.Type, out var adapter))
            throw GatewayException.BadRequest("unsupported_adapter", $"Adapter '{config.Type}' tidak tersedia.");

        var attempt = new Attempt(config, state, adapter, null);
        var tags = new TagList { { "operation", "test" }, { "provider", config.Type } };
        RequestsCounter.Add(1, tags);

        var sw = Stopwatch.StartNew();
        bool ok;
        using var testDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        testDeadline.CancelAfter(TimeSpan.FromSeconds(_options.ProviderTimeoutSeconds));
        try
        {
            var apiKey = protector.Unprotect(db.CurrentTenantId, config.ApiKeyEncrypted);
            var client = clientFactory.CreateClient("SearchProvider." + config.Type.ToLowerInvariant());
            ok = await adapter.TestAsync(config, apiKey, client, testDeadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            sw.Stop();
            var classified = ClassifyException(config, ex);
            FailureCounter.Add(1, tags);
            log.LogWarning("Web search {Operation} on {ProviderName} ({ProviderType}) failed in {LatencyMs} ms (outcome={Classification}, failover={Failover}, failoverReason={FailoverReason}).",
                "test", config.Name, config.Type, sw.ElapsedMilliseconds, classified.ErrorType, false, classified.ErrorType.ToString());

            if (classified.ErrorType == SearchErrorType.ClientError)
                await RecordFailureStatsAsync(attempt.State.Id, attempt.State.AdminGeneration, classified.SanitizedMessage, Now);
            else
                await RecordFailureAsync(attempt, classified, null, Now);

            return new ProviderTestResult(false, "Uji koneksi gagal: " + FixedMessage(classified.ErrorType));
        }

        sw.Stop();
        if (!ok)
        {
            FailureCounter.Add(1, tags);
            await RecordFailureAsync(attempt, new ClassifiedError(SearchErrorType.RetryableServer, null, FixedMessage(SearchErrorType.RetryableServer)), null, Now);
            return new ProviderTestResult(false, "Uji koneksi gagal: " + FixedMessage(SearchErrorType.RetryableServer));
        }

        // Mulai titik ini upstream sudah dipanggil dan ditagih; kegagalan pencatatan dibiarkan naik (500),
        // kredit tidak boleh hilang dan status tidak boleh dilaporkan sukses palsu.
        SuccessCounter.Add(1, tags);
        await RecordSuccessAsync(attempt, adapter.EstimateSearchCost(config, TestRequest, 1), Now);
        log.LogInformation("Web search {Operation} on {ProviderName} ({ProviderType}) succeeded in {LatencyMs} ms (outcome={Outcome}, failover={Failover}, failoverReason={FailoverReason}).",
            "test", config.Name, config.Type, sw.ElapsedMilliseconds, "success", false, "none");
        return new ProviderTestResult(true, "Kunci API valid dan terverifikasi.");
    }

    private sealed record Candidate(SearchProviderConfig Config, SearchProviderState State);

    private sealed record Attempt(SearchProviderConfig Config, SearchProviderState State, ISearchProvider Adapter, Guid? LeaseToken);

    private readonly record struct FailureTransition(bool Changed, string? Status, DateTime? Until);

    private readonly record struct Rollover(DateTime NewPeriodStart, DateTime Cutoff);

    private async Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken ct,
        Func<ISearchProvider, SearchProviderConfig, string, HttpClient, CancellationToken, Task<TResponse>> invoke,
        Func<ISearchProvider, SearchProviderConfig, TRequest, TResponse, decimal> estimateCost,
        Func<ISearchProvider, SearchProviderConfig, TRequest, Exception, decimal> partialCost)
    {
        var candidates = await LoadCandidatesAsync(operation, ct);
        var tried = new HashSet<long>();
        var attempts = 0;

        while (true)
        {
            var remaining = candidates.Where(c => !tried.Contains(c.Config.Id)).ToList();
            if (remaining.Count == 0)
                throw await BuildUnavailableExceptionAsync(candidates, ct);

            var now = Now;
            var eligible = remaining.Where(c => IsEligible(c.Config, c.State, now)).ToList();
            if (eligible.Count == 0)
                throw await BuildUnavailableExceptionAsync(candidates, ct);

            var chosen = PickWeighted(eligible);
            var attempt = await PrepareAttemptAsync(chosen, operation, now, ct);
            if (attempt is null)
            {
                // Kalah balapan sewa probe atau status berubah sejak snapshot; jangan panggil penyedia ini lagi.
                tried.Add(chosen.Config.Id);
                continue;
            }

            var tags = new TagList { { "operation", operation }, { "provider", attempt.Config.Type } };
            if (attempts++ > 0) FailoverCounter.Add(1, tags);
            RequestsCounter.Add(1, tags);

            var sw = Stopwatch.StartNew();
            TResponse response;
            // Deadline seluruh operasi: search/extract/test dibatasi ProviderTimeoutSeconds, jadi satu trial
            // (termasuk extract multi-URL) tidak mungkin melewati masa sewa probe.
            using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptDeadline.CancelAfter(TimeSpan.FromSeconds(_options.ProviderTimeoutSeconds));
            try
            {
                var apiKey = protector.Unprotect(db.CurrentTenantId, attempt.Config.ApiKeyEncrypted);
                var client = clientFactory.CreateClient("SearchProvider." + attempt.Config.Type.ToLowerInvariant());
                response = await invoke(attempt.Adapter, attempt.Config, apiKey, client, attemptDeadline.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                sw.Stop();

                // Kredit yang sudah terpakai upstream (extract parsial) dibukukan sebelum klasifikasi/failover,
                // tidak bisa dibatalkan cancellation, dan kegagalan pencatatan naik sebagai 500 tanpa failover.
                var spent = partialCost(attempt.Adapter, attempt.Config, request, ex);
                if (spent != 0m)
                {
                    var spentAt = Now;
                    await db.SearchProviderStates
                        .Where(s => s.Id == attempt.State.Id && s.TenantId == db.CurrentTenantId)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(s => s.UsedThisPeriod, s => s.UsedThisPeriod + spent)
                            .SetProperty(s => s.UpdatedAt, spentAt), CancellationToken.None);
                }

                // Pembatalan klien yang membawa kredit parsial: kredit tercatat di atas, lalu cancellation asli
                // diteruskan tanpa status gagal dan tanpa failover. Deadline operasi hub (ct tidak cancelled)
                // tetap lewat jalur klasifikasi normal.
                if (ct.IsCancellationRequested)
                {
                    if (ex is SearchProviderTransportException { InnerException: OperationCanceledException inner })
                        ExceptionDispatchInfo.Capture(inner).Throw();

                    throw new OperationCanceledException("Permintaan dibatalkan.", ex, ct);
                }

                var classified = ClassifyException(attempt.Config, ex);
                FailureCounter.Add(1, tags);

                if (classified.ErrorType == SearchErrorType.ClientError)
                {
                    log.LogWarning("Web search {Operation} on {ProviderName} ({ProviderType}) rejected the request in {LatencyMs} ms (outcome={Outcome}, failover={Failover}, failoverReason={FailoverReason}).",
                        operation, attempt.Config.Name, attempt.Config.Type, sw.ElapsedMilliseconds, "client_error", false, "client_error");
                    await RecordFailureStatsAsync(attempt.State.Id, attempt.State.AdminGeneration, classified.SanitizedMessage, Now);
                    throw new GatewayException(StatusCodes.Status400BadRequest, "provider_client_error", classified.SanitizedMessage);
                }

                var explicitRetryAfter = ex is SearchProviderException { RetryAfter: { } retryAfter } ? retryAfter : (TimeSpan?)null;
                var transition = await RecordFailureAsync(attempt, classified, explicitRetryAfter, Now);
                tried.Add(attempt.Config.Id);

                if (transition.Changed)
                {
                    log.LogWarning("Web search {Operation} on {ProviderName} ({ProviderType}) transitioned to {Status} until {UnavailableUntil} after {Classification} in {LatencyMs} ms (outcome={Classification}, failover={Failover}, failoverReason={FailoverReason}).",
                        operation, attempt.Config.Name, attempt.Config.Type, transition.Status, transition.Until, classified.ErrorType, sw.ElapsedMilliseconds,
                        classified.ErrorType, attempts > 1, classified.ErrorType.ToString());
                }
                else
                {
                    log.LogDebug("Web search {Operation} on {ProviderName} ({ProviderType}) failed in {LatencyMs} ms (outcome={Classification}, failover={Failover}, failoverReason={FailoverReason}).",
                        operation, attempt.Config.Name, attempt.Config.Type, sw.ElapsedMilliseconds, classified.ErrorType, attempts > 1, classified.ErrorType.ToString());
                }

                continue;
            }

            // Mulai titik ini upstream sudah dipanggil dan ditagih: kegagalan pencatatan tidak boleh memicu
            // failover (penyedia lain akan ditagih dua kali) dan kredit tidak boleh hilang saat request dibatalkan.
            sw.Stop();
            var cost = estimateCost(attempt.Adapter, attempt.Config, request, response);
            var cleared = await RecordSuccessAsync(attempt, cost, Now);
            SuccessCounter.Add(1, tags);

            if (attempt.LeaseToken is not null || (cleared && attempt.State.Status is not SearchProviderStatuses.Available))
            {
                log.LogInformation("Web search {Operation} on {ProviderName} ({ProviderType}) recovered in {LatencyMs} ms (outcome={Outcome}, failover={Failover}, failoverReason={FailoverReason}).",
                    operation, attempt.Config.Name, attempt.Config.Type, sw.ElapsedMilliseconds, "recovered", attempts > 1, "probe_recovery");
            }
            else
            {
                log.LogDebug("Web search {Operation} on {ProviderName} ({ProviderType}) succeeded in {LatencyMs} ms (outcome={Outcome}, failover={Failover}, failoverReason={FailoverReason}).",
                    operation, attempt.Config.Name, attempt.Config.Type, sw.ElapsedMilliseconds, "success", attempts > 1, "none");
            }

            return response;
        }
    }

    private async Task<List<Candidate>> LoadCandidatesAsync(string operation, CancellationToken ct)
    {
        var configs = await db.SearchProviderConfigs.AsNoTracking()
            .Where(c => c.IsEnabled && c.DeletedAt == null)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
        if (configs.Count == 0) return [];

        var ids = configs.Select(c => c.Id).ToList();
        var states = await db.SearchProviderStates.AsNoTracking()
            .Where(s => ids.Contains(s.ProviderId))
            .ToDictionaryAsync(s => s.ProviderId, ct);

        var list = new List<Candidate>(configs.Count);
        foreach (var config in configs)
        {
            if (!HasCapability(config.Capabilities, operation)) continue;

            var now = Now;
            if (!states.TryGetValue(config.Id, out var state))
                state = await EnsureStateAsync(config, now, ct);

            if (DueRollover(config, state, now) is { } due)
                state = await RolloverAsync(state, due, now, ct);

            list.Add(new Candidate(config, state));
        }

        return list;
    }

    private static bool HasCapability(string capabilities, string operation) =>
        capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(operation, StringComparer.OrdinalIgnoreCase);

    /// <summary>Buat baris state yang hilang; kalah balapan unik provider_id berarti baris milik proses lain.</summary>
    private async Task<SearchProviderState> EnsureStateAsync(SearchProviderConfig config, DateTime now, CancellationToken ct)
    {
        var state = new SearchProviderState
        {
            TenantId = config.TenantId,
            ProviderId = config.Id,
            Status = SearchProviderStatuses.Available,
            PeriodStart = now,
            UpdatedAt = now,
        };
        db.SearchProviderStates.Add(state);
        try
        {
            await db.SaveChangesAsync(ct);
            return state;
        }
        catch (DbUpdateException)
        {
            db.Entry(state).State = EntityState.Detached;
            var existing = await db.SearchProviderStates.AsNoTracking().FirstOrDefaultAsync(s => s.ProviderId == config.Id, ct);
            if (existing is not null) return existing;
            throw;
        }
    }

    private static Rollover? DueRollover(SearchProviderConfig config, SearchProviderState state, DateTime now) => config.ResetPolicy switch
    {
        ResetPolicies.FirstOfMonthUtc => BeginningOfMonth(now) is { } monthStart && state.PeriodStart < monthStart
            ? new Rollover(monthStart, monthStart)
            : null,
        ResetPolicies.Rolling30Days => state.PeriodStart < now.AddDays(-30)
            ? new Rollover(now, now.AddDays(-30))
            : null,
        _ => null,
    };

    private static DateTime BeginningOfMonth(DateTime now) => new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Rollover atomik: hanya satu proses menang lewat syarat period_start; yang kalah membaca ulang state.</summary>
    private async Task<SearchProviderState> RolloverAsync(SearchProviderState state, Rollover due, DateTime now, CancellationToken ct)
    {
        var affected = await db.SearchProviderStates
            .Where(s => s.Id == state.Id && s.TenantId == db.CurrentTenantId && s.PeriodStart < due.Cutoff)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.UsedThisPeriod, 0m)
                .SetProperty(s => s.PeriodStart, due.NewPeriodStart)
                .SetProperty(s => s.Status, s => s.Status == SearchProviderStatuses.Exhausted ? SearchProviderStatuses.Available : s.Status)
                .SetProperty(s => s.UnavailableUntil, s => s.Status == SearchProviderStatuses.Exhausted ? (DateTime?)null : s.UnavailableUntil)
                .SetProperty(s => s.ConsecutiveFailures, s => s.Status == SearchProviderStatuses.Exhausted ? 0 : s.ConsecutiveFailures)
                .SetProperty(s => s.LastError, s => s.Status == SearchProviderStatuses.Exhausted ? (string?)null : s.LastError)
                .SetProperty(s => s.Generation, s => s.Generation + 1)
                .SetProperty(s => s.UpdatedAt, now), ct);

        if (affected == 0)
            return await db.SearchProviderStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == state.Id, ct) ?? state;

        state.UsedThisPeriod = 0;
        state.PeriodStart = due.NewPeriodStart;
        if (state.Status == SearchProviderStatuses.Exhausted)
        {
            state.Status = SearchProviderStatuses.Available;
            state.UnavailableUntil = null;
            state.ConsecutiveFailures = 0;
            state.LastError = null;
        }
        state.Generation += 1;
        state.UpdatedAt = now;
        return state;
    }

    private bool IsEligible(SearchProviderConfig config, SearchProviderState state, DateTime now)
    {
        if (state.Status is SearchProviderStatuses.Disabled or SearchProviderStatuses.Broken) return false;
        // ponytail: check-then-book tanpa reservasi kredit; burst paralel bisa sedikit melewati BudgetThreshold.
        // Naikkan dengan klaim kredit atomik (reserved column) bila overshoot anggaran harus nol.
        if (config.MonthlyBudget is { } budget && state.UsedThisPeriod >= budget * _options.BudgetThreshold) return false;
        if (state.UnavailableUntil is { } until && until > now) return false;
        return true;
    }

    private static bool NeedsProbe(SearchProviderState state) =>
        state.Status is SearchProviderStatuses.RateLimited or SearchProviderStatuses.Exhausted;

    /// <summary>
    /// Baca state terbaru (RetryAfter/status bisa berubah sejak snapshot) lalu, untuk penyedia yang sedang
    /// cooldown, klaim sewa probe tunggal secara atomik. Hasil null = penyedia tidak boleh dipanggil.
    /// </summary>
    private async Task<Attempt?> PrepareAttemptAsync(Candidate candidate, string operation, DateTime now, CancellationToken ct)
    {
        var fresh = await LoadFreshProviderAsync(candidate.Config.Id, ct);
        if (fresh is not { } pair || !IsEligible(pair.Config, pair.State, now) || !HasCapability(pair.Config.Capabilities, operation)) return null;

        var config = pair.Config;
        var state = pair.State;
        if (!_adapters.TryGetValue(config.Type, out var adapter))
        {
            log.LogError("Web search adapter missing for {ProviderName} ({ProviderType}); provider skipped.", config.Name, config.Type);
            return null;
        }

        if (!NeedsProbe(state)) return new Attempt(config, state, adapter, null);

        var token = Guid.NewGuid();
        var claimed = await db.SearchProviderStates
            .Where(s => s.Id == state.Id && s.TenantId == db.CurrentTenantId
                && s.AdminGeneration == state.AdminGeneration
                && (s.Status == SearchProviderStatuses.RateLimited || s.Status == SearchProviderStatuses.Exhausted)
                && (s.UnavailableUntil == null || s.UnavailableUntil <= now)
                && (s.ProbeLeaseId == null || s.ProbeLeaseUntil == null || s.ProbeLeaseUntil <= now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.ProbeLeaseId, token)
                .SetProperty(s => s.ProbeLeaseUntil, now.AddSeconds(_options.ProbeLeaseSeconds))
                .SetProperty(s => s.UpdatedAt, now), ct);

        if (claimed == 0)
        {
            log.LogDebug("Web search probe lease for {ProviderName} ({ProviderType}) is held by another request or config changed; provider skipped (failoverReason={FailoverReason}).",
                config.Name, config.Type, "probe_lease_held");
            return null;
        }

        return new Attempt(config, state, adapter, token);
    }

    /// <summary>
    /// Pasangan config+state yang konsisten: admin menulis config dan AdminGeneration dalam satu transaksi,
    /// jadi baca ulang AdminGeneration membuktikan config yang dipakai bukan versi kunci lama. Retry singkat
    /// menutup jendela balapan; panggilan upstream tidak pernah terjadi di dalam transaksi.
    /// </summary>
    private async Task<(SearchProviderConfig Config, SearchProviderState State)?> LoadFreshProviderAsync(long providerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = await db.SearchProviderStates.AsNoTracking().FirstOrDefaultAsync(s => s.ProviderId == providerId, ct);
            if (state is null) return null;

            var config = await db.SearchProviderConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == providerId && c.DeletedAt == null, ct);
            if (config is null) return null;

            var adminGeneration = await db.SearchProviderStates.AsNoTracking()
                .Where(s => s.Id == state.Id)
                .Select(s => s.AdminGeneration)
                .FirstAsync(ct);
            if (adminGeneration == state.AdminGeneration) return (config, state);
        }

        return null;
    }

    private Candidate PickWeighted(List<Candidate> candidates)
    {
        if (candidates.Count == 1) return candidates[0];

        var totalWeight = candidates.Sum(c => Math.Max(1, c.Config.Weight));
        var roll = random.Next(totalWeight);
        var cumulative = 0;
        foreach (var c in candidates)
        {
            cumulative += Math.Max(1, c.Config.Weight);
            if (roll < cumulative) return c;
        }
        return candidates[^1];
    }

    /// <summary>Akuntansi sukses tidak bisa dibatalkan cancellation setelah panggilan upstream.</summary>
    private async Task<bool> RecordSuccessAsync(Attempt attempt, decimal cost, DateTime now)
    {
        var cleared = attempt.LeaseToken is { } token
            ? await UpdateLeasedSuccessAsync(attempt.State.Id, token, attempt.State.AdminGeneration, cost, now) == 1
            : await UpdateNormalSuccessAsync(attempt.State.Id, attempt.State.Generation, attempt.State.AdminGeneration, cost, now) == 1;

        if (cleared) return true;

        // Status berubah oleh kegagalan/admin sejak pemilihan: kredit tetap dibukukan, status tidak disentuh.
        await db.SearchProviderStates
            .Where(s => s.Id == attempt.State.Id && s.TenantId == db.CurrentTenantId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.LastSuccessAt, now)
                .SetProperty(s => s.SuccessCount, s => s.SuccessCount + 1)
                .SetProperty(s => s.UsedThisPeriod, s => s.UsedThisPeriod + cost)
                .SetProperty(s => s.UpdatedAt, now), CancellationToken.None);

        if (attempt.LeaseToken is { } held)
        {
            await db.SearchProviderStates
                .Where(s => s.Id == attempt.State.Id && s.TenantId == db.CurrentTenantId && s.ProbeLeaseId == held)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.ProbeLeaseId, (Guid?)null)
                    .SetProperty(s => s.ProbeLeaseUntil, (DateTime?)null), CancellationToken.None);
        }

        return false;
    }

    private Task<int> UpdateNormalSuccessAsync(long stateId, long generation, long adminGeneration, decimal cost, DateTime now) =>
        db.SearchProviderStates
            .Where(s => s.Id == stateId && s.TenantId == db.CurrentTenantId
                && s.Status != SearchProviderStatuses.Disabled
                && s.Generation == generation && s.AdminGeneration == adminGeneration)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, SearchProviderStatuses.Available)
                .SetProperty(s => s.UnavailableUntil, (DateTime?)null)
                .SetProperty(s => s.ConsecutiveFailures, 0)
                .SetProperty(s => s.LastError, (string?)null)
                .SetProperty(s => s.LastSuccessAt, now)
                .SetProperty(s => s.SuccessCount, s => s.SuccessCount + 1)
                .SetProperty(s => s.UsedThisPeriod, s => s.UsedThisPeriod + cost)
                .SetProperty(s => s.UpdatedAt, now), CancellationToken.None);

    private Task<int> UpdateLeasedSuccessAsync(long stateId, Guid leaseToken, long adminGeneration, decimal cost, DateTime now) =>
        db.SearchProviderStates
            .Where(s => s.Id == stateId && s.TenantId == db.CurrentTenantId
                && s.Status != SearchProviderStatuses.Disabled
                && s.ProbeLeaseId == leaseToken && s.AdminGeneration == adminGeneration)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, SearchProviderStatuses.Available)
                .SetProperty(s => s.UnavailableUntil, (DateTime?)null)
                .SetProperty(s => s.ConsecutiveFailures, 0)
                .SetProperty(s => s.LastError, (string?)null)
                .SetProperty(s => s.LastSuccessAt, now)
                .SetProperty(s => s.SuccessCount, s => s.SuccessCount + 1)
                .SetProperty(s => s.UsedThisPeriod, s => s.UsedThisPeriod + cost)
                .SetProperty(s => s.ProbeLeaseId, (Guid?)null)
                .SetProperty(s => s.ProbeLeaseUntil, (DateTime?)null)
                .SetProperty(s => s.UpdatedAt, now), CancellationToken.None);

    /// <summary>Kesalahan klien dicatat ke statistik saja: status/cooldown tidak diturunkan dan tidak failover.</summary>
    private Task<int> RecordFailureStatsAsync(long stateId, long adminGeneration, string sanitizedMessage, DateTime now) =>
        db.SearchProviderStates
            .Where(s => s.Id == stateId && s.TenantId == db.CurrentTenantId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.FailureCount, s => s.FailureCount + 1)
                .SetProperty(s => s.LastError, s => s.AdminGeneration == adminGeneration ? sanitizedMessage : s.LastError)
                .SetProperty(s => s.UpdatedAt, now), CancellationToken.None);

    private async Task<FailureTransition> RecordFailureAsync(Attempt attempt, ClassifiedError classified, TimeSpan? explicitRetryAfter, DateTime now)
    {
        var stateId = attempt.State.Id;
        var adminGeneration = attempt.State.AdminGeneration;
        var leaseToken = attempt.LeaseToken;
        var targetStatus = classified.ErrorType switch
        {
            SearchErrorType.Exhausted => SearchProviderStatuses.Exhausted,
            SearchErrorType.RateLimited => SearchProviderStatuses.RateLimited,
            SearchErrorType.Broken => SearchProviderStatuses.Broken,
            _ => null,
        };
        var targetUntil = classified.ErrorType switch
        {
            SearchErrorType.Exhausted => CalculateNextReset(attempt.Config.ResetPolicy, attempt.State.PeriodStart, now),
            // Retry-After eksplisit selalu menang; tanpa itu cooldown dasar berlipat dua tiap 429 beruntun (maks 32x).
            SearchErrorType.RateLimited => now.Add(explicitRetryAfter
                ?? TimeSpan.FromSeconds(_options.RetryAfterFallbackSeconds * RateLimitBackoffMultiplier(attempt.State.ConsecutiveFailures))),
            _ => (DateTime?)null,
        };
        var isServerFailure = classified.ErrorType == SearchErrorType.RetryableServer;
        var countsAsConsecutiveFailure = isServerFailure || classified.ErrorType == SearchErrorType.RateLimited;
        var threshold = _options.CircuitFailureThreshold;
        var circuitUntil = now.AddSeconds(_options.CircuitCooldownSeconds);
        var hasTargetUntil = targetUntil.HasValue;
        var newUntilValue = targetUntil ?? circuitUntil;

        // 1) Counter selalu naik dan backoff diperpanjang (tidak pernah dipendekkan); baris status belum diubah,
        //    jadi predikat transisi di langkah 2 melihat jumlah kegagalan beruntun yang sudah committed.
        await db.SearchProviderStates
            .Where(s => s.Id == stateId && s.TenantId == db.CurrentTenantId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.FailureCount, s => s.FailureCount + 1)
                .SetProperty(s => s.FailoverCount, s => s.FailoverCount + 1)
                .SetProperty(s => s.LastError, s => s.AdminGeneration == adminGeneration ? classified.SanitizedMessage : s.LastError)
                .SetProperty(s => s.ConsecutiveFailures, s => countsAsConsecutiveFailure && s.AdminGeneration == adminGeneration ? s.ConsecutiveFailures + 1 : s.ConsecutiveFailures)
                .SetProperty(s => s.UnavailableUntil, s =>
                    s.AdminGeneration != adminGeneration || s.Status == SearchProviderStatuses.Disabled || s.Status == SearchProviderStatuses.Broken
                        ? s.UnavailableUntil
                        : (hasTargetUntil || (isServerFailure && s.Status == SearchProviderStatuses.RateLimited)) && (s.UnavailableUntil == null || s.UnavailableUntil.Value < newUntilValue)
                            ? newUntilValue
                            : s.UnavailableUntil)
                .SetProperty(s => s.Generation, s => s.Generation + 1)
                .SetProperty(s => s.ProbeLeaseId, s => leaseToken != null && s.ProbeLeaseId == leaseToken ? (Guid?)null : s.ProbeLeaseId)
                .SetProperty(s => s.ProbeLeaseUntil, s => leaseToken != null && s.ProbeLeaseId == leaseToken ? (DateTime?)null : s.ProbeLeaseUntil)
                .SetProperty(s => s.UpdatedAt, now), CancellationToken.None);

        // 2) Transisi status hanya oleh penulis pertama yang lolos predikat pada baris committed; itulah
        //    satu-satunya penulis yang memancarkan peringatan transisi. Status exhausted tidak diturunkan
        //    rate limit, dan transisi yang basi setelah perubahan admin tidak pernah diterapkan.
        var transitioned = targetStatus is not null
            ? await TryTransitionAsync(stateId, adminGeneration, targetStatus, targetUntil, now, targetStatus switch
            {
                SearchProviderStatuses.Exhausted => s => s.Status != SearchProviderStatuses.Disabled && s.Status != SearchProviderStatuses.Broken,
                SearchProviderStatuses.RateLimited => s => s.Status != SearchProviderStatuses.Disabled && s.Status != SearchProviderStatuses.Broken && s.Status != SearchProviderStatuses.Exhausted,
                _ => s => s.Status != SearchProviderStatuses.Disabled,
            })
            : isServerFailure && await TryTransitionAsync(stateId, adminGeneration, SearchProviderStatuses.RateLimited, circuitUntil, now,
                s => s.Status != SearchProviderStatuses.Disabled && s.Status != SearchProviderStatuses.Broken
                    && s.Status != SearchProviderStatuses.Exhausted && s.ConsecutiveFailures >= threshold);

        return transitioned
            ? new FailureTransition(true, targetStatus ?? SearchProviderStatuses.RateLimited, targetStatus is not null ? targetUntil : circuitUntil)
            : new FailureTransition(false, null, null);
    }

    /// <summary>Transisi status yang menang balapan; 0 baris berarti penulis lain/admin sudah mengubahnya.</summary>
    private async Task<bool> TryTransitionAsync(
        long stateId,
        long adminGeneration,
        string targetStatus,
        DateTime? targetUntil,
        DateTime now,
        Expression<Func<SearchProviderState, bool>> overrideAllowed)
    {
        var affected = await db.SearchProviderStates
            .Where(s => s.Id == stateId && s.TenantId == db.CurrentTenantId
                && s.AdminGeneration == adminGeneration
                && s.Status != targetStatus)
            .Where(overrideAllowed)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, targetStatus)
                .SetProperty(s => s.UnavailableUntil, targetUntil)
                .SetProperty(s => s.Generation, s => s.Generation + 1)
                .SetProperty(s => s.UpdatedAt, now), CancellationToken.None);

        return affected == 1;
    }

    /// <summary>Backoff 429 berulang tanpa Retry-After: 1x, 2x, 4x, ... maksimum 32x cooldown dasar.</summary>
    private static int RateLimitBackoffMultiplier(int consecutiveFailures) =>
        1 << Math.Min(5, Math.Max(0, consecutiveFailures));

    private DateTime CalculateNextReset(string resetPolicy, DateTime periodStart, DateTime now) => resetPolicy switch
    {
        ResetPolicies.FirstOfMonthUtc => BeginningOfMonth(now).AddMonths(1),
        ResetPolicies.Rolling30Days => periodStart.AddDays(30),
        // Manual: tidak ada tanggal reset; periksa ulang kuota setelah jeda recheck.
        _ => now.AddHours(_options.QuotaRecheckHours),
    };

    private static string FixedMessage(SearchErrorType errorType) => errorType switch
    {
        SearchErrorType.Broken => "Kunci API tidak valid atau dicabut.",
        SearchErrorType.Exhausted => "Kuota atau kredit penyedia habis.",
        SearchErrorType.RateLimited => "Penyedia sedang membatasi permintaan (rate limited).",
        SearchErrorType.RetryableServer => "Penyedia tidak dapat dihubungi.",
        SearchErrorType.ClientError => "Penyedia menolak permintaan uji.",
        _ => "Uji koneksi gagal.",
    };

    private ClassifiedError ClassifyException(SearchProviderConfig config, Exception ex)
    {
        if (ex is SearchProviderException spe)
        {
            return SearchErrorClassifier.Classify(
                config.Type, spe.ResponseStatusCode, spe.ResponseBody, spe.RetryAfter, spe, config.ErrorMappingOverride);
        }
        return SearchErrorClassifier.Classify(
            config.Type, null, null, null, ex, config.ErrorMappingOverride);
    }

    /// <summary>
    /// 503 memakai state terbaru dari database: snapshot bisa basi ketika cooldown/half-open baru saja
    /// diperpanjang atau dilepas proses lain, sehingga Retry-After harus dari baris terbaru.
    /// </summary>
    private async Task<GatewayException> BuildUnavailableExceptionAsync(List<Candidate> candidates, CancellationToken ct)
    {
        var now = Now;
        var ids = candidates.Select(c => c.Config.Id).ToList();
        var upcoming = new List<DateTime>();
        if (ids.Count > 0)
        {
            var horizons = await db.SearchProviderStates.AsNoTracking()
                .Where(s => ids.Contains(s.ProviderId))
                .Select(s => new { s.UnavailableUntil, s.ProbeLeaseUntil })
                .ToListAsync(ct);

            upcoming = horizons
                .SelectMany(h => new[] { h.UnavailableUntil, h.ProbeLeaseUntil })
                .Where(d => d is { } value && value > now && value < DateTime.MaxValue)
                .Select(d => d!.Value)
                .ToList();
        }

        var retryAfterSec = _options.RetryAfterFallbackSeconds;
        if (upcoming.Count > 0)
            retryAfterSec = Math.Max(1, (int)Math.Ceiling((upcoming.Min() - now).TotalSeconds));

        var ex = new GatewayException(
            StatusCodes.Status503ServiceUnavailable,
            "no_search_providers_available",
            "Semua penyedia pencarian web saat ini tidak tersedia atau kuota habis. Silakan coba lagi nanti.");
        ex.Data["RetryAfter"] = retryAfterSec.ToString(CultureInfo.InvariantCulture);
        return ex;
    }
}
