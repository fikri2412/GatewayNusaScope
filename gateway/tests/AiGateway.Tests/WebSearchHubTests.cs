using System.Diagnostics;
using System.Net;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Security;
using AiGateway.Core.WebSearch;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiGateway.Tests;

public sealed class WebSearchHubTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private Tenant _tenant = null!;
    private ProviderKeyProtector _protector = null!;

    private static readonly DateTimeOffset Base = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tenant = await _testDb.NewTenantAsync();
        // Provider kunci ephemeral: tidak menulis keyring ke disk, jadi tidak ada direktori temp yang bocor.
        _protector = new ProviderKeyProtector(new EphemeralDataProtectionProvider());
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ScriptedRandom(params int[] rolls) : IRandomProvider
    {
        private int _index;
        public int Next(int maxValue)
        {
            var r = rolls[_index % rolls.Length];
            _index++;
            return r % maxValue;
        }
    }

    private sealed class MockAdapter(
        string type,
        Func<SearchRequest, Task<SearchResponse>> searchHandler,
        Func<ExtractRequest, Task<ExtractResponse>>? extractHandler = null,
        Func<Task<bool>>? testHandler = null) : ISearchProvider
    {
        public string Type => type;
        public SearchCapabilities Capabilities => SearchCapabilities.All;

        public Task<SearchResponse> SearchAsync(SearchProviderConfig config, string apiKey, SearchRequest request, HttpClient client, CancellationToken ct) =>
            searchHandler(request);

        public Task<ExtractResponse> ExtractAsync(SearchProviderConfig config, string apiKey, ExtractRequest request, HttpClient client, CancellationToken ct) =>
            extractHandler != null ? extractHandler(request) : Task.FromResult(new ExtractResponse(Type, []));

        public Task<bool> TestAsync(SearchProviderConfig config, string apiKey, HttpClient client, CancellationToken ct) =>
            testHandler != null ? testHandler() : Task.FromResult(true);
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private WebSearchHub NewHub(
        GatewayDbContext db,
        IEnumerable<ISearchProvider> adapters,
        IRandomProvider random,
        TimeProvider clock,
        WebSearchOptions? options = null,
        ILogger<WebSearchHub>? logger = null) =>
        new(db, _protector, new MockHttpClientFactory(), adapters, random, clock,
            Options.Create(options ?? new WebSearchOptions()), logger ?? NullLogger<WebSearchHub>.Instance);

    private sealed class CapturingLogger : ILogger<WebSearchHub>
    {
        private readonly List<string> _warnings = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            lock (_warnings) _warnings.Add(formatter(state, exception));
        }

        public int Warnings(string fragment)
        {
            lock (_warnings) return _warnings.Count(m => m.Contains(fragment, StringComparison.Ordinal));
        }
    }

    private async Task<SearchProviderConfig> AddConfigAsync(
        GatewayDbContext db,
        string type,
        string name,
        int weight = 1,
        decimal costSearch = 1m,
        decimal costExtract = 1m,
        decimal? budget = null,
        string resetPolicy = ResetPolicies.FirstOfMonthUtc,
        string capabilities = "search,extract")
    {
        var config = new SearchProviderConfig
        {
            TenantId = _tenant.Id,
            Name = name,
            Type = type,
            BaseUri = "https://api.example.com",
            ApiKeyEncrypted = _protector.Protect(_tenant.Id, "key-" + name),
            KeyHint = "****" + name[^Math.Min(4, name.Length)..],
            Weight = weight,
            Capabilities = capabilities,
            CostPerSearch = costSearch,
            CostPerExtract = costExtract,
            MonthlyBudget = budget,
            ResetPolicy = resetPolicy,
            IsEnabled = true,
        };
        db.SearchProviderConfigs.Add(config);
        await db.SaveChangesAsync();
        return config;
    }

    private static SearchProviderState NewState(
        long tenantId,
        long providerId,
        string status = SearchProviderStatuses.Available,
        DateTime? unavailableUntil = null,
        decimal usedThisPeriod = 0m,
        DateTime? periodStart = null,
        int consecutiveFailures = 0,
        long successCount = 0,
        long failureCount = 0,
        string? lastError = null,
        Guid? probeLeaseId = null,
        DateTime? probeLeaseUntil = null,
        long adminGeneration = 0) => new()
    {
        TenantId = tenantId,
        ProviderId = providerId,
        Status = status,
        UnavailableUntil = unavailableUntil,
        UsedThisPeriod = usedThisPeriod,
        PeriodStart = periodStart ?? Base.UtcDateTime,
        ConsecutiveFailures = consecutiveFailures,
        SuccessCount = successCount,
        FailureCount = failureCount,
        LastError = lastError,
        ProbeLeaseId = probeLeaseId,
        ProbeLeaseUntil = probeLeaseUntil,
        AdminGeneration = adminGeneration,
    };

    private async Task<SearchProviderState> StateAsync(long providerId)
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        return await db.SearchProviderStates.AsNoTracking().FirstAsync(s => s.ProviderId == providerId);
    }

    [Fact]
    public async Task Weighted_selection_picks_higher_weighted_provider()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfgA = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Provider A", weight: 1);
        var cfgB = await AddConfigAsync(db, SearchProviderTypes.Exa, "Provider B", weight: 3);

        var adapterA = new MockAdapter(SearchProviderTypes.Tavily, _ => Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, [])));
        var adapterB = new MockAdapter(SearchProviderTypes.Exa, _ => Task.FromResult(new SearchResponse(SearchProviderTypes.Exa, [])));

        // Weight A=1, B=3 => total 4; roll 2 memilih B (1 <= 2 < 4), urutan kandidat deterministik by id.
        var hub = NewHub(db, [adapterA, adapterB], new ScriptedRandom(2), new MutableTimeProvider(Base));
        var result = await hub.SearchAsync(new SearchRequest("test"), default);

        Assert.Equal(SearchProviderTypes.Exa, result.Provider);
        var stateA = await StateAsync(cfgA.Id);
        var stateB = await StateAsync(cfgB.Id);
        Assert.Equal(0, stateA.SuccessCount);
        Assert.Equal(1, stateB.SuccessCount);
    }

    [Fact]
    public async Task Failover_skips_failed_provider_and_never_calls_it_twice()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfgA = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Exhausted A", weight: 10, costSearch: 2m);
        var cfgB = await AddConfigAsync(db, SearchProviderTypes.Exa, "Healthy B", weight: 1);

        var callsA = 0;
        var callsB = 0;
        var adapterA = new MockAdapter(SearchProviderTypes.Tavily, _ =>
        {
            callsA++;
            throw new SearchProviderException((HttpStatusCode)432, "quota exceeded");
        });
        var adapterB = new MockAdapter(SearchProviderTypes.Exa, _ =>
        {
            callsB++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Exa, [new SearchResultItem("Title", "https://example.com", "snippet")]));
        });

        var clock = new MutableTimeProvider(Base);
        var hub = NewHub(db, [adapterA, adapterB], new ScriptedRandom(0), clock);

        var result1 = await hub.SearchAsync(new SearchRequest("query 1"), default);
        Assert.Equal(SearchProviderTypes.Exa, result1.Provider);
        Assert.Equal(1, callsA);
        Assert.Equal(1, callsB);

        var stateA = await StateAsync(cfgA.Id);
        Assert.Equal(SearchProviderStatuses.Exhausted, stateA.Status);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), stateA.UnavailableUntil);
        Assert.Equal(1, stateA.FailureCount);
        Assert.Equal(1, stateA.FailoverCount);
        Assert.Equal(0, stateA.UsedThisPeriod);

        // Request berikutnya: A masih cooldown, tidak dipanggil lagi.
        var result2 = await hub.SearchAsync(new SearchRequest("query 2"), default);
        Assert.Equal(SearchProviderTypes.Exa, result2.Provider);
        Assert.Equal(1, callsA);
        Assert.Equal(2, callsB);

        var stateB = await StateAsync(cfgB.Id);
        Assert.Equal(2, stateB.SuccessCount);
        Assert.Equal(2m, stateB.UsedThisPeriod);
        Assert.Equal(0, stateB.FailureCount);
    }

    [Fact]
    public async Task All_providers_unavailable_returns_503_with_RetryAfter()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Down Provider");
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.RateLimited,
            unavailableUntil: Base.UtcDateTime.AddSeconds(45)));
        await db.SaveChangesAsync();

        var calls = 0;
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
        {
            calls++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, []));
        });

        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("test"), default));

        Assert.Equal(503, ex.Status);
        Assert.Equal("no_search_providers_available", ex.Code);
        Assert.Equal("45", ex.Data["RetryAfter"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Circuit_breaker_trips_after_configured_consecutive_failures()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Firecrawl, "Failing Provider");

        var adapter = new MockAdapter(SearchProviderTypes.Firecrawl, _ =>
            throw new HttpRequestException("Network failure", null, HttpStatusCode.InternalServerError));

        var options = new WebSearchOptions { CircuitFailureThreshold = 3, CircuitCooldownSeconds = 90 };
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base), options);

        await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("query 1"), default));
        await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("query 2"), default));
        await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("query 3"), default));

        var state = await StateAsync(cfg.Id);
        Assert.Equal(3, state.ConsecutiveFailures);
        Assert.Equal(3, state.FailureCount);
        Assert.Equal(SearchProviderStatuses.RateLimited, state.Status);
        Assert.Equal(Base.UtcDateTime.AddSeconds(90), state.UnavailableUntil);
    }

    [Fact]
    public async Task Concurrent_requests_record_exact_totals()
    {
        const int requestCount = 10;
        const decimal costPerSearch = 2.5m;
        long providerId;
        await using (var db = _testDb.NewContext(_tenant.Id))
        {
            var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Concurrency Provider", costSearch: costPerSearch);
            providerId = cfg.Id;
        }

        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ => Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, [])));

        var tasks = Enumerable.Range(0, requestCount).Select(async _ =>
        {
            await using var isolatedDb = _testDb.NewContext(_tenant.Id);
            var hub = NewHub(isolatedDb, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
            return await hub.SearchAsync(new SearchRequest("concurrent"), default);
        });

        var responses = await Task.WhenAll(tasks);
        Assert.Equal(requestCount, responses.Length);

        var state = await StateAsync(providerId);
        Assert.Equal(requestCount, state.SuccessCount);
        Assert.Equal(costPerSearch * requestCount, state.UsedThisPeriod);
        Assert.Equal(0, state.FailureCount);
        Assert.Equal(0, state.FailoverCount);
    }

    [Fact]
    public async Task Half_open_lease_allows_only_one_trial_while_probe_is_in_flight()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "HalfOpen", costSearch: 2m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.RateLimited,
            unavailableUntil: Base.UtcDateTime.AddMinutes(-1), usedThisPeriod: 10m, consecutiveFailures: 3));
        await db.SaveChangesAsync();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return new SearchResponse(SearchProviderTypes.Tavily, []);
        });
        var clock = new MutableTimeProvider(Base);

        await using var probeDb = _testDb.NewContext(_tenant.Id);
        var probeHub = NewHub(probeDb, [adapter], new ScriptedRandom(0), clock);
        var probeTask = probeHub.SearchAsync(new SearchRequest("probe"), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await using var secondDb = _testDb.NewContext(_tenant.Id);
        var secondHub = NewHub(secondDb, [adapter], new ScriptedRandom(0), clock);
        var ex = await Assert.ThrowsAsync<GatewayException>(() => secondHub.SearchAsync(new SearchRequest("second"), default));
        Assert.Equal(503, ex.Status);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.TrySetResult();
        var probeResponse = await probeTask;
        Assert.Equal(SearchProviderTypes.Tavily, probeResponse.Provider);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(12m, state.UsedThisPeriod);
        Assert.Null(state.ProbeLeaseId);
        Assert.Null(state.ProbeLeaseUntil);
    }

    [Fact]
    public async Task Expired_probe_lease_allows_new_trial()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "ExpiredLease", costSearch: 2m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.RateLimited,
            unavailableUntil: Base.UtcDateTime.AddMinutes(-1), usedThisPeriod: 4m, consecutiveFailures: 2,
            probeLeaseId: Guid.NewGuid(), probeLeaseUntil: Base.UtcDateTime.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var calls = 0;
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
        {
            calls++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, []));
        });
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));

        var result = await hub.SearchAsync(new SearchRequest("probe"), default);
        Assert.Equal(SearchProviderTypes.Tavily, result.Provider);
        Assert.Equal(1, calls);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
        Assert.Null(state.ProbeLeaseId);
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(6m, state.UsedThisPeriod);
    }

    [Fact]
    public async Task Probe_failure_extends_retry_after_and_fails_over()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfgA = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Tripped A", weight: 10, costSearch: 3m);
        var cfgB = await AddConfigAsync(db, SearchProviderTypes.Exa, "Healthy B", weight: 1);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfgA.Id, SearchProviderStatuses.RateLimited,
            unavailableUntil: Base.UtcDateTime.AddMinutes(-1), consecutiveFailures: 3, failureCount: 4));
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfgB.Id));
        await db.SaveChangesAsync();

        var callsA = 0;
        var callsB = 0;
        var adapterA = new MockAdapter(SearchProviderTypes.Tavily, _ =>
        {
            callsA++;
            throw new SearchProviderException(HttpStatusCode.TooManyRequests, "slow down", TimeSpan.FromSeconds(120));
        });
        var adapterB = new MockAdapter(SearchProviderTypes.Exa, _ =>
        {
            callsB++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Exa, []));
        });

        var hub = NewHub(db, [adapterA, adapterB], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var result = await hub.SearchAsync(new SearchRequest("q"), default);

        Assert.Equal(SearchProviderTypes.Exa, result.Provider);
        Assert.Equal(1, callsA);
        Assert.Equal(1, callsB);

        var stateA = await StateAsync(cfgA.Id);
        Assert.Equal(SearchProviderStatuses.RateLimited, stateA.Status);
        Assert.Equal(Base.UtcDateTime.AddSeconds(120), stateA.UnavailableUntil);
        Assert.Null(stateA.ProbeLeaseId);
        Assert.Equal(5, stateA.FailureCount);
        Assert.Equal(0m, stateA.UsedThisPeriod); // kegagalan tidak menambah kredit terpakai

        var stateB = await StateAsync(cfgB.Id);
        Assert.Equal(1, stateB.SuccessCount);
        Assert.Equal(1m, stateB.UsedThisPeriod);
    }

    [Fact]
    public async Task Recovery_probe_after_cooldown_restores_provider()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Recovered", costSearch: 2m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.RateLimited,
            unavailableUntil: Base.UtcDateTime.AddMinutes(-1), usedThisPeriod: 5m, consecutiveFailures: 3,
            failureCount: 3, lastError: "Server upstream error (HTTP 500)."));
        await db.SaveChangesAsync();

        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, [])));
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));

        var result = await hub.SearchAsync(new SearchRequest("probe"), default);
        Assert.Equal(SearchProviderTypes.Tavily, result.Provider);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
        Assert.Null(state.UnavailableUntil);
        Assert.Null(state.LastError);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(7m, state.UsedThisPeriod);
    }

    [Fact]
    public async Task Period_rollover_resets_exhausted_usage()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 5, 0, TimeSpan.Zero));
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Rollover", costSearch: 3m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.Exhausted,
            unavailableUntil: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), usedThisPeriod: 500m,
            periodStart: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), failureCount: 2, lastError: "Kuota habis."));
        await db.SaveChangesAsync();

        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, [])));
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), clock);

        var result = await hub.SearchAsync(new SearchRequest("after rollover"), default);
        Assert.Equal(SearchProviderTypes.Tavily, result.Provider);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
        Assert.Null(state.UnavailableUntil);
        Assert.Null(state.LastError);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), state.PeriodStart);
        Assert.Equal(3m, state.UsedThisPeriod);
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(2, state.FailureCount);
    }

    [Fact]
    public async Task Budget_threshold_blocks_provider_at_configured_fraction()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Budgeted", budget: 100m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, usedThisPeriod: 95m));
        await db.SaveChangesAsync();

        var calls = 0;
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
        {
            calls++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, []));
        });

        var blockedHub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => blockedHub.SearchAsync(new SearchRequest("blocked"), default));
        Assert.Equal(503, ex.Status);
        Assert.Equal(0, calls);

        var relaxedHub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base),
            new WebSearchOptions { BudgetThreshold = 0.96m });
        var result = await relaxedHub.SearchAsync(new SearchRequest("allowed"), default);
        Assert.Equal(SearchProviderTypes.Tavily, result.Provider);
        Assert.Equal(1, calls);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(96m, state.UsedThisPeriod);
    }

    [Fact]
    public async Task In_flight_success_does_not_overwrite_newer_failure_state()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Racing", costSearch: 2m);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowAdapter = new MockAdapter(SearchProviderTypes.Tavily, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return new SearchResponse(SearchProviderTypes.Tavily, []);
        });

        await using var slowDb = _testDb.NewContext(_tenant.Id);
        var slowHub = NewHub(slowDb, [slowAdapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var slowTask = slowHub.SearchAsync(new SearchRequest("slow"), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var failingAdapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));
        await using var failingDb = _testDb.NewContext(_tenant.Id);
        var failingHub = NewHub(failingDb, [failingAdapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<GatewayException>(() => failingHub.SearchAsync(new SearchRequest("fail"), default));

        release.TrySetResult();
        var slowResponse = await slowTask;
        Assert.Equal(SearchProviderTypes.Tavily, slowResponse.Provider);

        var state = await StateAsync(cfg.Id);
        // Kegagalan yang lebih baru menang: sukses lama hanya membukukan kredit, bukan memulihkan status.
        Assert.Equal(SearchProviderStatuses.RateLimited, state.Status);
        Assert.Equal(3, state.ConsecutiveFailures);
        Assert.Equal(3, state.FailureCount);
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(2m, state.UsedThisPeriod);
    }

    [Fact]
    public async Task Missing_state_row_is_created_and_accounted()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "NoStateYet", costSearch: 4m);

        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, [])));
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));

        var result = await hub.SearchAsync(new SearchRequest("fresh"), default);
        Assert.Equal(SearchProviderTypes.Tavily, result.Provider);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
        Assert.Equal(Base.UtcDateTime, state.PeriodStart);
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(4m, state.UsedThisPeriod);
    }

    [Fact]
    public async Task Broken_provider_is_skipped_without_calls()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Broken");
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.Broken,
            lastError: "Kunci API tidak valid atau dicabut."));
        await db.SaveChangesAsync();

        var calls = 0;
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
        {
            calls++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, []));
        });
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));

        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("test"), default));
        Assert.Equal(503, ex.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Extract_cost_counts_requested_urls_and_capability_filter_is_exact()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var extractOnly = await AddConfigAsync(db, SearchProviderTypes.Tavily, "ExtractOnly",
            weight: 10, costExtract: 2m, capabilities: "extract");
        var searchOnly = await AddConfigAsync(db, SearchProviderTypes.Exa, "SearchOnly",
            weight: 10, costExtract: 1m, capabilities: "search");
        db.SearchProviderStates.AddRange(NewState(_tenant.Id, extractOnly.Id), NewState(_tenant.Id, searchOnly.Id));
        await db.SaveChangesAsync();

        var extractCalls = 0;
        var searchCalls = 0;
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            {
                searchCalls++;
                return Task.FromResult(new SearchResponse(SearchProviderTypes.Tavily, []));
            },
            extractHandler: req =>
            {
                extractCalls++;
                return Task.FromResult(new ExtractResponse(SearchProviderTypes.Tavily,
                    req.Urls.Select(u => new ExtractedPage(u, "markdown")).ToList()));
            });
        var searchAdapter = new MockAdapter(SearchProviderTypes.Exa, _ =>
        {
            searchCalls++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Exa, []));
        });

        var hub = NewHub(db, [adapter, searchAdapter], new ScriptedRandom(19), new MutableTimeProvider(Base));
        var response = await hub.ExtractAsync(new ExtractRequest(["https://a.example", "https://b.example", "https://c.example"]), default);

        Assert.Equal(3, response.Pages.Count);
        Assert.Equal(1, extractCalls);
        Assert.Equal(0, searchCalls);

        var state = await StateAsync(extractOnly.Id);
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(6m, state.UsedThisPeriod);

        var searchOnlyState = await StateAsync(searchOnly.Id);
        Assert.Equal(0, searchOnlyState.SuccessCount);
        Assert.Equal(0m, searchOnlyState.UsedThisPeriod);
    }

    [Fact]
    public async Task Admin_test_reuses_same_accounting_and_fixed_messages()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var healthy = await AddConfigAsync(db, SearchProviderTypes.Tavily, "TestHealthy", costSearch: 3m);
        var broken = await AddConfigAsync(db, SearchProviderTypes.Exa, "TestBroken");
        db.SearchProviderStates.Add(NewState(_tenant.Id, healthy.Id, SearchProviderStatuses.Broken,
            lastError: "Kunci API tidak valid atau dicabut."));
        db.SearchProviderStates.Add(NewState(_tenant.Id, broken.Id, SearchProviderStatuses.Broken));
        await db.SaveChangesAsync();

        var healthyAdapter = new MockAdapter(SearchProviderTypes.Tavily, _ => throw new InvalidOperationException("search tidak dipakai uji"),
            testHandler: () => Task.FromResult(true));
        var brokenAdapter = new MockAdapter(SearchProviderTypes.Exa, _ => throw new InvalidOperationException("search tidak dipakai uji"),
            testHandler: () => throw new SearchProviderException(HttpStatusCode.Unauthorized, "invalid api key sk-123"));

        var hub = NewHub(db, [healthyAdapter, brokenAdapter], new ScriptedRandom(0), new MutableTimeProvider(Base));

        var ok = await hub.TestAsync(healthy.PublicId, default);
        Assert.True(ok.Success);

        var fail = await hub.TestAsync(broken.PublicId, default);
        Assert.False(fail.Success);
        Assert.Contains("Kunci API", fail.Message);
        Assert.DoesNotContain("sk-123", fail.Message);

        var healthyState = await StateAsync(healthy.Id);
        Assert.Equal(SearchProviderStatuses.Available, healthyState.Status);
        Assert.Null(healthyState.LastError);
        Assert.Equal(1, healthyState.SuccessCount);
        Assert.Equal(3m, healthyState.UsedThisPeriod);

        var brokenState = await StateAsync(broken.Id);
        Assert.Equal(SearchProviderStatuses.Broken, brokenState.Status);
        Assert.Equal(1, brokenState.FailureCount);
        Assert.Equal(0, brokenState.SuccessCount);
    }

    [Fact]
    public async Task Operation_deadline_cancels_hanging_provider()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Hanging");

        var adapter = new HangingAdapter();
        var options = new WebSearchOptions { ProviderTimeoutSeconds = 1 };
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base), options);

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("hang"), default));
        sw.Stop();

        Assert.Equal(503, ex.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"deadline operasi tidak memotong penyedia yang menggantung ({sw.Elapsed})");

        var state = await StateAsync(cfg.Id);
        Assert.Equal(1, state.FailureCount);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
        Assert.Null(state.UnavailableUntil);
    }

    [Fact]
    public async Task Client_error_records_failure_stats_without_downgrade_or_failover()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfgA = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Rejecting A", weight: 10);
        var cfgB = await AddConfigAsync(db, SearchProviderTypes.Exa, "Healthy B", weight: 10);

        var callsB = 0;
        var adapterA = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            throw new SearchProviderException(HttpStatusCode.BadRequest, "bad query secret-body"));
        var adapterB = new MockAdapter(SearchProviderTypes.Exa, _ =>
        {
            callsB++;
            return Task.FromResult(new SearchResponse(SearchProviderTypes.Exa, []));
        });

        var hub = NewHub(db, [adapterA, adapterB], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("bad"), default));

        Assert.Equal(400, ex.Status);
        Assert.Equal("provider_client_error", ex.Code);
        Assert.DoesNotContain("secret-body", ex.Message);
        Assert.Equal(0, callsB); // kesalahan klien tidak boleh failover

        var stateA = await StateAsync(cfgA.Id);
        Assert.Equal(1, stateA.FailureCount);
        Assert.Contains("400", stateA.LastError!);
        Assert.Equal(SearchProviderStatuses.Available, stateA.Status);
        Assert.Equal(0, stateA.ConsecutiveFailures);
        Assert.Null(stateA.UnavailableUntil);
        Assert.Equal(0, stateA.FailoverCount);
        Assert.Equal(0, stateA.SuccessCount);

        var stateB = await StateAsync(cfgB.Id);
        Assert.Equal(0, stateB.SuccessCount);
        Assert.Equal(0, stateB.FailureCount);
    }

    [Fact]
    public async Task Stale_failure_after_admin_key_rotation_does_not_downgrade_state()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Rotating");
        var providerId = cfg.Id;

        // Admin merotasi kunci + menaikkan AdminGeneration selagi request lama masih in-flight.
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ => RotateKeyThenFailAsync(providerId));

        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("stale"), default));
        Assert.Equal(503, ex.Status);

        var state = await StateAsync(providerId);
        Assert.Equal(1, state.AdminGeneration);
        Assert.Equal(SearchProviderStatuses.Available, state.Status); // kunci lama tidak menandai kunci baru broken
        Assert.Null(state.LastError);
        Assert.Equal(1, state.FailureCount); // counter tetap dibukukan
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    private async Task<SearchResponse> RotateKeyThenFailAsync(long providerId)
    {
        await using var admin = _testDb.NewContext(_tenant.Id);
        var config = await admin.SearchProviderConfigs.FirstAsync(c => c.Id == providerId);
        config.ApiKeyEncrypted = _protector.Protect(_tenant.Id, "new-key");
        var state = await admin.SearchProviderStates.FirstAsync(s => s.ProviderId == providerId);
        state.AdminGeneration += 1;
        state.LastError = null;
        await admin.SaveChangesAsync();
        throw new SearchProviderException(HttpStatusCode.Unauthorized, "old key rejected");
    }

    [Fact]
    public async Task Rate_limit_failure_does_not_downgrade_exhausted_status()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "ExhaustedQuota");
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.Exhausted,
            unavailableUntil: Base.UtcDateTime.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            throw new SearchProviderException(HttpStatusCode.TooManyRequests, "slow down", TimeSpan.FromSeconds(120)));

        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("probe"), default));
        Assert.Equal(503, ex.Status);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(SearchProviderStatuses.Exhausted, state.Status);
        Assert.Equal(Base.UtcDateTime.AddSeconds(120), state.UnavailableUntil); // RetryAfter terbaru menang
        Assert.Equal(1, state.FailureCount);
    }

    [Fact]
    public async Task Concurrent_quota_failures_transition_once_and_count_all()
    {
        const int requestCount = 5;
        long providerId;
        await using (var db = _testDb.NewContext(_tenant.Id))
        {
            var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "QuotaStorm");
            providerId = cfg.Id;
        }

        var adapter = new CountingFailureAdapter(requestCount, () => new SearchProviderException((HttpStatusCode)432, "quota exceeded"));
        var logger = new CapturingLogger();

        var tasks = Enumerable.Range(0, requestCount).Select(async _ =>
        {
            await using var isolated = _testDb.NewContext(_tenant.Id);
            var hub = NewHub(isolated, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base), null, logger);
            await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("quota"), default));
        });
        await Task.WhenAll(tasks);

        var state = await StateAsync(providerId);
        Assert.Equal(requestCount, state.FailureCount);
        Assert.Equal(SearchProviderStatuses.Exhausted, state.Status);
        Assert.Equal(1, logger.Warnings("transitioned to"));
    }

    [Fact]
    public async Task Concurrent_server_failures_trip_circuit_once_with_flat_cooldown()
    {
        const int requestCount = 3;
        long providerId;
        await using (var db = _testDb.NewContext(_tenant.Id))
        {
            var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "TripStorm");
            providerId = cfg.Id;
        }

        var adapter = new CountingFailureAdapter(requestCount,
            () => new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger();

        var tasks = Enumerable.Range(0, requestCount).Select(async _ =>
        {
            await using var isolated = _testDb.NewContext(_tenant.Id);
            var hub = NewHub(isolated, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base), null, logger);
            await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("trip"), default));
        });
        await Task.WhenAll(tasks);

        var state = await StateAsync(providerId);
        Assert.Equal(requestCount, state.FailureCount);
        Assert.Equal(requestCount, state.ConsecutiveFailures);
        Assert.Equal(SearchProviderStatuses.RateLimited, state.Status);
        Assert.Equal(Base.UtcDateTime.AddSeconds(60), state.UnavailableUntil);
        Assert.Equal(1, logger.Warnings("transitioned to"));
    }

    [Fact]
    public async Task Partial_extract_spend_is_recorded_before_failover()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfgA = await AddConfigAsync(db, SearchProviderTypes.Tavily, "Partial A", weight: 10, costExtract: 7m);
        var cfgB = await AddConfigAsync(db, SearchProviderTypes.Exa, "Healthy B", weight: 1, costExtract: 1m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfgA.Id));
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfgB.Id));
        await db.SaveChangesAsync();

        var adapterA = new MockAdapter(SearchProviderTypes.Tavily,
            _ => throw new InvalidOperationException("search tidak dipakai"),
            extractHandler: _ => throw new SearchProviderException(HttpStatusCode.TooManyRequests, "rate limited",
                TimeSpan.FromSeconds(30), successfulPageCount: 4));
        var callsB = 0;
        var adapterB = new MockAdapter(SearchProviderTypes.Exa,
            _ => throw new InvalidOperationException("search tidak dipakai"),
            extractHandler: req =>
            {
                callsB++;
                return Task.FromResult(new ExtractResponse(SearchProviderTypes.Exa,
                    req.Urls.Select(u => new ExtractedPage(u, "markdown")).ToList()));
            });

        var hub = NewHub(db, [adapterA, adapterB], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var response = await hub.ExtractAsync(new ExtractRequest(["https://a.example", "https://b.example"]), default);

        Assert.Equal(SearchProviderTypes.Exa, response.Provider);
        Assert.Equal(2, response.Pages.Count);
        Assert.Equal(1, callsB);

        var stateA = await StateAsync(cfgA.Id);
        Assert.Equal(28m, stateA.UsedThisPeriod); // 4 halaman terbayar sebelum 429 tetap dibukukan
        Assert.Equal(1, stateA.FailureCount);
        Assert.Equal(SearchProviderStatuses.RateLimited, stateA.Status);

        var stateB = await StateAsync(cfgB.Id);
        Assert.Equal(2m, stateB.UsedThisPeriod);
    }

    [Fact]
    public async Task Repeated_rate_limit_without_retry_after_doubles_cooldown_capped()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "RateLimitedRepeated");
        var clock = new MutableTimeProvider(Base);
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            throw new SearchProviderException(HttpStatusCode.TooManyRequests, "slow down"));
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), clock);

        // 429 beruntun tanpa Retry-After: 60, 120, 240, 480, 960, 1920, lalu tetap 1920 (cap 32x).
        var expectedDelays = new[] { 60, 120, 240, 480, 960, 1920, 1920 };
        foreach (var delay in expectedDelays)
        {
            var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("429"), default));
            Assert.Equal(503, ex.Status);

            var state = await StateAsync(cfg.Id);
            Assert.Equal(SearchProviderStatuses.RateLimited, state.Status);
            Assert.Equal(clock.UtcNow.UtcDateTime.AddSeconds(delay), state.UnavailableUntil);

            clock.UtcNow = state.UnavailableUntil!.Value.AddSeconds(1); // cooldown lewat -> probe berikutnya
        }
    }

    [Fact]
    public async Task Explicit_retry_after_wins_over_backoff()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "ExplicitRetryAfter");
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.RateLimited,
            unavailableUntil: Base.UtcDateTime.AddMinutes(-1), consecutiveFailures: 6));
        await db.SaveChangesAsync();

        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ =>
            throw new SearchProviderException(HttpStatusCode.TooManyRequests, "slow down", TimeSpan.FromSeconds(15)));

        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("429"), default));
        Assert.Equal(503, ex.Status);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(Base.UtcDateTime.AddSeconds(15), state.UnavailableUntil); // bukan 60 x 32
    }

    [Fact]
    public async Task Partial_credit_on_caller_cancellation_is_kept_without_failover()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfgA = await AddConfigAsync(db, SearchProviderTypes.Tavily, "PartialCancel A", weight: 10, costExtract: 5m);
        var cfgB = await AddConfigAsync(db, SearchProviderTypes.Exa, "Healthy B", weight: 1);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfgA.Id));
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfgB.Id));
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        var callsB = 0;
        var adapterA = new MockAdapter(SearchProviderTypes.Tavily,
            _ => throw new InvalidOperationException("search tidak dipakai"),
            extractHandler: _ =>
            {
                cts.Cancel();
                throw new SearchProviderTransportException(2, new OperationCanceledException(cts.Token));
            });
        var adapterB = new MockAdapter(SearchProviderTypes.Exa,
            _ => throw new InvalidOperationException("search tidak dipakai"),
            extractHandler: req =>
            {
                callsB++;
                return Task.FromResult(new ExtractResponse(SearchProviderTypes.Exa, []));
            });

        var hub = NewHub(db, [adapterA, adapterB], new ScriptedRandom(0), new MutableTimeProvider(Base));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hub.ExtractAsync(new ExtractRequest(["https://a.example", "https://b.example"]), cts.Token));

        Assert.Equal(0, callsB); // pembatalan klien tidak boleh failover
        var stateA = await StateAsync(cfgA.Id);
        Assert.Equal(10m, stateA.UsedThisPeriod); // 2 halaman terbayar x 5 tetap dibukukan
        Assert.Equal(0, stateA.FailureCount);
        Assert.Equal(SearchProviderStatuses.Available, stateA.Status);
    }

    [Fact]
    public async Task Zero_page_caller_cancellation_records_nothing()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "CancelNoSpend", costExtract: 5m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id));
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        var adapter = new MockAdapter(SearchProviderTypes.Tavily,
            _ => throw new InvalidOperationException("search tidak dipakai"),
            extractHandler: _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hub.ExtractAsync(new ExtractRequest(["https://a.example"]), cts.Token));

        var state = await StateAsync(cfg.Id);
        Assert.Equal(0m, state.UsedThisPeriod);
        Assert.Equal(0, state.FailureCount);
        Assert.Equal(SearchProviderStatuses.Available, state.Status);
    }

    [Fact]
    public async Task Admin_test_on_disabled_provider_validates_without_enabling()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "DisabledProvider", costSearch: 3m);
        db.SearchProviderStates.Add(NewState(_tenant.Id, cfg.Id, SearchProviderStatuses.Disabled));
        await db.SaveChangesAsync();

        var adapter = new MockAdapter(SearchProviderTypes.Tavily,
            _ => throw new InvalidOperationException("search tidak dipakai"),
            testHandler: () => Task.FromResult(true));
        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));

        var result = await hub.TestAsync(cfg.PublicId, default);

        Assert.True(result.Success);
        var state = await StateAsync(cfg.Id);
        Assert.Equal(SearchProviderStatuses.Disabled, state.Status); // tetap disabled, tidak dihidupkan
        Assert.Equal(1, state.SuccessCount);
        Assert.Equal(3m, state.UsedThisPeriod);
    }

    [Fact]
    public async Task Stale_client_error_does_not_overwrite_admin_last_error()
    {
        await using var db = _testDb.NewContext(_tenant.Id);
        var cfg = await AddConfigAsync(db, SearchProviderTypes.Tavily, "StaleClientError");
        var adapter = new MockAdapter(SearchProviderTypes.Tavily, _ => RotateAdminStateThenThrowClientErrorAsync(cfg.Id));

        var hub = NewHub(db, [adapter], new ScriptedRandom(0), new MutableTimeProvider(Base));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => hub.SearchAsync(new SearchRequest("bad"), default));
        Assert.Equal(400, ex.Status);
        Assert.Equal("provider_client_error", ex.Code);

        var state = await StateAsync(cfg.Id);
        Assert.Equal(1, state.AdminGeneration);
        Assert.Equal(1, state.FailureCount); // counter tetap dibukukan
        Assert.Null(state.LastError);        // LastError admin tidak ditimpa error basi
    }

    private async Task<SearchResponse> RotateAdminStateThenThrowClientErrorAsync(long providerId)
    {
        await using var admin = _testDb.NewContext(_tenant.Id);
        var state = await admin.SearchProviderStates.FirstAsync(s => s.ProviderId == providerId);
        state.AdminGeneration += 1;
        state.LastError = null;
        await admin.SaveChangesAsync();
        throw new SearchProviderException(HttpStatusCode.BadRequest, "bad query");
    }

    /// <summary>Semua panggilan masuk menunggu sampai jumlah target tercapai, lalu gagal bersamaan.</summary>
    private sealed class CountingFailureAdapter(int target, Func<Exception> failure) : ISearchProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public string Type => SearchProviderTypes.Tavily;
        public SearchCapabilities Capabilities => SearchCapabilities.All;

        public async Task<SearchResponse> SearchAsync(SearchProviderConfig config, string apiKey, SearchRequest request, HttpClient client, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _entered) >= target) _gate.TrySetResult();
            await _gate.Task;
            throw failure();
        }

        public Task<ExtractResponse> ExtractAsync(SearchProviderConfig config, string apiKey, ExtractRequest request, HttpClient client, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> TestAsync(SearchProviderConfig config, string apiKey, HttpClient client, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class HangingAdapter : ISearchProvider
    {
        public string Type => SearchProviderTypes.Tavily;
        public SearchCapabilities Capabilities => SearchCapabilities.All;

        public async Task<SearchResponse> SearchAsync(SearchProviderConfig config, string apiKey, SearchRequest request, HttpClient client, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            throw new InvalidOperationException("deadline operasi harus membatalkan panggilan ini");
        }

        public Task<ExtractResponse> ExtractAsync(SearchProviderConfig config, string apiKey, ExtractRequest request, HttpClient client, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> TestAsync(SearchProviderConfig config, string apiKey, HttpClient client, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
