using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using AiGateway.Core.Data.Migrations;
using AiGateway.Core.Domain;
using AiGateway.Core.Maintenance;
using AiGateway.Core.Proxy;
using AiGateway.Core.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiGateway.Tests;

/// <summary>
/// GA-03: usage yang belum lengkap tidak diklaim nol pasti. Adaptor mempertahankan angka yang benar-benar
/// dilaporkan upstream (input Anthropic dari <c>message_start</c>, output dari <c>message_delta</c>) walau stream
/// terputus, penanda kelengkapan disimpan di log + agregat harian, dan laporan/retensi mempertahankannya.
///
/// Pembaca adaptor diakses lewat refleksi supaya test ini tetap bisa dikompilasi dan dijalankan terhadap snapshot
/// adapter sebelum perubahan (bukti failing-before) tanpa mereferensikan API baru (<c>UsageSnapshot</c>/
/// <c>UsageIncomplete</c>) secara langsung.
/// </summary>
public class UsageCompletenessTests(TestDb db) : GatewayTestBase(db)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // ----------------------------------------- adaptor -----------------------------------------

    private static object Adapter(string family)
    {
        var adapters = typeof(Provider).Assembly.GetType("AiGateway.Core.Proxy.FamilyAdapters", throwOnError: true)!;
        return adapters.GetMethod("For", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [family])!;
    }

    private static TextReader Translated(string family, string upstreamSse)
    {
        var adapter = Adapter(family);
        var translate = adapter.GetType().GetMethod("TranslateStream", BindingFlags.Public | BindingFlags.Instance)!;
        return (TextReader)translate.Invoke(adapter, [new StringReader(upstreamSse)])!;
    }

    private static JsonObject? ChatCompletion(string family, string upstreamJson)
    {
        var adapter = Adapter(family);
        var toChat = adapter.GetType().GetMethod("ToChatCompletion", BindingFlags.Public | BindingFlags.Instance)!;
        return (JsonObject?)toChat.Invoke(adapter, [JsonNode.Parse(upstreamJson)]);
    }

    /// <summary>Usage terakhir yang diketahui pembaca; null bila propertinya tidak ada (snapshot sebelum perubahan).</summary>
    private static JsonObject? Snapshot(object reader) =>
        reader.GetType().GetProperty("UsageSnapshot", BindingFlags.Public | BindingFlags.Instance)?.GetValue(reader) as JsonObject;

    private static bool? UsageIncomplete(object reader) =>
        reader.GetType().GetProperty("UsageIncomplete", BindingFlags.Public | BindingFlags.Instance)?.GetValue(reader) as bool?;

    private static async Task<List<string>> ReadAllAsync(TextReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>Chunk usage-saja terakhir di antara baris SSE terjemahan.</summary>
    private static JsonObject EmittedUsage(IEnumerable<string> lines) =>
        lines.Where(l => l.StartsWith("data: {", StringComparison.Ordinal))
            .Select(l => JsonNode.Parse(l[6..])!.AsObject())
            .Last(c => c["usage"] is not null)["usage"]!.AsObject();

    private static string Event(string type, string json) => $"event: {type}\ndata: {json}\n\n";

    private static long? Long(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<long>(out var wide) ? wide : v.TryGetValue<int>(out var count) ? count : null
        : null;

    [Fact]
    public async Task Anthropic_interruption_before_final_usage_keeps_known_input_without_zero_output()
    {
        var reader = Translated(ApiFamilies.AnthropicMessages,
            Event("message_start",
                """{"type":"message_start","message":{"id":"msg_1","model":"real","usage":{"input_tokens":10,"cache_read_input_tokens":4}}}"""));

        var lines = await ReadAllAsync(reader); // upstream menutup tanpa message_delta/message_stop

        Assert.DoesNotContain(lines, l => l == "data: [DONE]");
        var snapshot = Snapshot(reader);
        Assert.NotNull(snapshot);
        Assert.Equal(14L, Long(snapshot!["prompt_tokens"]));
        Assert.Equal(4L, Long(snapshot!["prompt_tokens_details"]!["cached_tokens"]));
        Assert.False(snapshot!.ContainsKey("completion_tokens"));
        Assert.False(snapshot!.ContainsKey("total_tokens"));
        Assert.Equal(true, UsageIncomplete(reader));
    }

    [Fact]
    public async Task Anthropic_message_delta_output_survives_eof_without_message_stop()
    {
        var reader = Translated(ApiFamilies.AnthropicMessages,
            Event("message_start",
                """{"type":"message_start","message":{"id":"msg_1","model":"real","usage":{"input_tokens":10,"cache_read_input_tokens":4,"output_tokens":1}}}""")
            + Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}"""));

        var lines = await ReadAllAsync(reader);

        Assert.Contains("data: [DONE]", lines);
        var usage = EmittedUsage(lines);
        Assert.Equal((14L, 7L, 21L), (Long(usage["prompt_tokens"]), Long(usage["completion_tokens"]), Long(usage["total_tokens"])));
        var snapshot = Snapshot(reader);
        Assert.NotNull(snapshot);
        Assert.Equal(7L, Long(snapshot!["completion_tokens"]));
        Assert.Equal(false, UsageIncomplete(reader));
    }

    [Fact]
    public async Task Anthropic_stream_that_never_reports_output_is_not_completed_with_a_zero()
    {
        var reader = Translated(ApiFamilies.AnthropicMessages,
            Event("message_start",
                """{"type":"message_start","message":{"id":"msg_1","model":"real","usage":{"input_tokens":10,"cache_read_input_tokens":4}}}""")
            + Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""") // alasan selesai ada (GA-11), output_tokens tidak
            + Event("message_stop", """{"type":"message_stop"}"""));

        var usage = EmittedUsage(await ReadAllAsync(reader));

        Assert.Equal((14L, 4L), (Long(usage["prompt_tokens"]), Long(usage["prompt_tokens_details"]!["cached_tokens"])));
        Assert.False(usage.ContainsKey("completion_tokens"));
        Assert.False(usage.ContainsKey("total_tokens"));
        Assert.Equal(true, UsageIncomplete(reader));
    }

    [Fact]
    public async Task Anthropic_malformed_usage_report_does_not_erase_known_values()
    {
        var reader = Translated(ApiFamilies.AnthropicMessages,
            Event("message_start",
                """{"type":"message_start","message":{"id":"msg_1","model":"real","usage":{"input_tokens":10,"cache_read_input_tokens":4}}}""")
            + Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":-1,"output_tokens":"bad","cache_read_input_tokens":9223372036854775807}}""")
            + Event("message_stop", """{"type":"message_stop"}"""));

        var usage = EmittedUsage(await ReadAllAsync(reader));

        Assert.Equal(14L, Long(usage["prompt_tokens"]));
        Assert.False(usage.ContainsKey("completion_tokens"));
        Assert.Equal(true, UsageIncomplete(reader));
    }

    [Fact]
    public async Task Responses_usage_reported_before_the_stream_dies_is_kept()
    {
        var reader = Translated(ApiFamilies.OpenAiResponses,
            Event("response.in_progress",
                """{"type":"response.in_progress","response":{"id":"resp_1","usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12,"output_tokens_details":{"reasoning_tokens":1}}}}"""));

        await ReadAllAsync(reader);

        var snapshot = Snapshot(reader);
        Assert.NotNull(snapshot);
        Assert.Equal((10L, 2L, 12L), (Long(snapshot!["prompt_tokens"]), Long(snapshot!["completion_tokens"]), Long(snapshot!["total_tokens"])));
        Assert.Equal(1L, Long(snapshot!["completion_tokens_details"]!["reasoning_tokens"]));
        Assert.Equal(true, UsageIncomplete(reader));
    }

    [Fact]
    public async Task Responses_completed_usage_is_complete_and_keeps_details()
    {
        var reader = Translated(ApiFamilies.OpenAiResponses,
            Event("response.completed",
                """
                {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[],
                 "usage":{"input_tokens":14,"input_tokens_details":{"cached_tokens":4},"output_tokens":7,
                          "output_tokens_details":{"reasoning_tokens":2},"total_tokens":21}}}
                """.ReplaceLineEndings("").Replace("  ", "")));

        var usage = EmittedUsage(await ReadAllAsync(reader));

        Assert.Equal((14L, 7L, 21L, 4L, 2L), (Long(usage["prompt_tokens"]), Long(usage["completion_tokens"]),
            Long(usage["total_tokens"]), Long(usage["prompt_tokens_details"]!["cached_tokens"]),
            Long(usage["completion_tokens_details"]!["reasoning_tokens"])));
        Assert.Equal(false, UsageIncomplete(reader));
    }

    [Fact]
    public void Rest_normalization_omits_counters_the_upstream_never_reported()
    {
        var anthropic = ChatCompletion(ApiFamilies.AnthropicMessages,
            """{"id":"msg_1","model":"real","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":10,"cache_read_input_tokens":2}}""")!["usage"]!.AsObject();
        Assert.Equal((12L, 2L), (Long(anthropic["prompt_tokens"]), Long(anthropic["prompt_tokens_details"]!["cached_tokens"])));
        Assert.False(anthropic.ContainsKey("completion_tokens"));
        Assert.False(anthropic.ContainsKey("total_tokens"));

        var responses = ChatCompletion(ApiFamilies.OpenAiResponses,
            """{"id":"resp_1","status":"completed","model":"real","output":[],"usage":{"input_tokens":10}}""")!["usage"]!.AsObject();
        Assert.Equal(10L, Long(responses["prompt_tokens"]));
        Assert.False(responses.ContainsKey("completion_tokens"));
        Assert.False(responses.ContainsKey("total_tokens"));
        Assert.False(responses.ContainsKey("prompt_tokens_details"));

        // Usage yang sama sekali tidak dikenali tidak menjadi objek berisi nol.
        var unknown = ChatCompletion(ApiFamilies.OpenAiResponses,
            """{"id":"resp_1","status":"completed","model":"real","output":[],"usage":{"weird":1}}""")!;
        Assert.False(unknown.ContainsKey("usage"));
    }

    // ------------------------------------ pencatatan & laporan ------------------------------------

    private static UsageLog Log(Scenario s, DateTime createdAt, int input, int output, string status, bool incomplete, int attempts = 1) => new()
    {
        TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = s.ModelId,
        Status = status, HttpStatus = 200, InputTokens = input, OutputTokens = output, CreatedAt = createdAt,
        UsageIncomplete = incomplete, Attempts = attempts,
    };

    private static MaintenanceOptions RetentionOptions() => new()
    {
        UsageLogRetentionDays = 7,
        RetentionMaxDaysPerRun = 7,
        JobRunRetentionDays = 7,
        DeliveryRetentionDays = 7,
    };

    [Fact]
    public async Task Recorder_persists_the_marker_and_counts_an_incomplete_request_once()
    {
        var s = await SetupAsync();

        await WithServicesAsync(s.TenantId, async (sp, _) =>
        {
            var recorder = sp.GetRequiredService<UsageRecorder>();
            await recorder.RecordAsync(Log(s, DateTime.UtcNow, 10, 0, UsageStatuses.Ok, incomplete: true));
            await recorder.RecordAsync(Log(s, DateTime.UtcNow, 5, 5, UsageStatuses.Ok, incomplete: false));
            return 0;
        });

        await using var check = Db.NewContext(s.TenantId);
        var day = await check.UsageDailies.AsNoTracking().SingleAsync();
        Assert.Equal((2L, 1L), (day.Requests, day.IncompleteUsageRequests));
        Assert.Equal(1, await check.UsageLogs.CountAsync(l => l.UsageIncomplete));
        Assert.Equal(1, await check.UsageLogs.CountAsync(l => !l.UsageIncomplete));
    }

    [Fact]
    public async Task Denied_requests_are_never_marked_incomplete()
    {
        var s = await SetupAsync();

        await WithServicesAsync(s.TenantId, async (sp, _) =>
        {
            var key = new ApiKey
            {
                Id = s.KeyId, TenantId = s.TenantId, ProjectId = s.ProjectId, Name = "k", KeyPrefix = "gw_1", KeyHash = "h",
            };
            await sp.GetRequiredService<UsageRecorder>()
                .RecordDeniedAsync(new GatewayCaller(key, Guid.NewGuid(), null, null, null), 429, "rate_limited");
            return 0;
        });

        await using var check = Db.NewContext(s.TenantId);
        var log = await check.UsageLogs.AsNoTracking().SingleAsync();
        Assert.Equal((UsageStatuses.Denied, false), (log.Status, log.UsageIncomplete));
        var day = await check.UsageDailies.AsNoTracking().SingleAsync();
        Assert.Equal((1L, 1L, 0L), (day.Requests, day.Denied, day.IncompleteUsageRequests));
    }

    [Fact]
    public async Task Reports_and_platform_overview_expose_the_incomplete_marker()
    {
        var s = await SetupAsync();

        await WithServicesAsync(s.TenantId, async (sp, _) =>
        {
            var recorder = sp.GetRequiredService<UsageRecorder>();
            await recorder.RecordAsync(Log(s, DateTime.UtcNow, 10, 0, UsageStatuses.Ok, incomplete: true));
            await recorder.RecordAsync(Log(s, DateTime.UtcNow, 5, 5, UsageStatuses.Ok, incomplete: false));
            return 0;
        });

        var from = Today.AddDays(-1);
        var summary = await WithServicesAsync(s.TenantId,
            (sp, _) => sp.GetRequiredService<UsageReports>().SummaryAsync(from, Today, "day", default));
        Assert.Equal((2L, 1L), (summary.Single().Requests, summary.Single().IncompleteUsageRequests));

        var logs = await WithServicesAsync(s.TenantId, (sp, _) => sp.GetRequiredService<UsageReports>()
            .LogsAsync(new UsageFilter(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)), 1, 50, default));
        Assert.Equal(1, logs.Items.Count(l => l.UsageIncomplete));

        var overview = await WithServicesAsync(s.TenantId,
            (sp, _) => sp.GetRequiredService<UsageReports>().PlatformOverviewAsync(from, Today, default));
        Assert.Equal(1L, overview.Single(r => r.Key == s.TenantId.ToString(CultureInfo.InvariantCulture)).IncompleteUsageRequests);
    }

    [Fact]
    public async Task Retention_reconciliation_preserves_incomplete_counts()
    {
        var s = await SetupAsync();
        var oldDay = DateTime.UtcNow.Date.AddDays(-30);

        await WithServicesAsync(s.TenantId, async (sp, ctx) =>
        {
            var recorder = sp.GetRequiredService<UsageRecorder>();
            await recorder.RecordAsync(Log(s, oldDay.AddHours(1), 10, 2, UsageStatuses.Ok, incomplete: true));
            await recorder.RecordAsync(Log(s, oldDay.AddHours(2), 0, 0, UsageStatuses.Denied, incomplete: false, attempts: 0));

            // Agregat basi: penanda hilang kalau rekonsiliasi tidak ikut menghitung ulang.
            await ctx.UsageDailies.Where(u => u.Day == DateOnly.FromDateTime(oldDay))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IncompleteUsageRequests, 9L).SetProperty(x => x.Requests, 99L));

            var outcome = await new RetentionService(
                ctx, Options.Create(RetentionOptions()), TimeProvider.System, NullLogger<RetentionService>.Instance).RunAsync(default);
            Assert.Equal(1, outcome.DaysReconciled);
            Assert.True(outcome.LogsDeleted >= 2, $"logs={outcome.LogsDeleted}");
            return 0;
        });

        await using var check = Db.NewContext(s.TenantId);
        var day = await check.UsageDailies.AsNoTracking().SingleAsync(u => u.Day == DateOnly.FromDateTime(oldDay));
        Assert.Equal((2L, 1L, 10L, 2L, 1L), (day.Requests, day.Denied, day.InputTokens, day.OutputTokens, day.IncompleteUsageRequests));
        Assert.False(await check.UsageLogs.AnyAsync(l => l.CreatedAt < oldDay.AddDays(1)));
    }

    // ----------------------------------- backfill migration -----------------------------------

    /// <summary><c>Up</c> dilindungi; subclass ini hanya mengambil SQL-nya untuk dieksekusi apa adanya.</summary>
    private sealed class BackfillProbe : GA03UsageCompleteness
    {
        public IReadOnlyList<string> Statements()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            Up(builder);
            return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
        }
    }

    private static UsageLog Legacy(Scenario s, DateTime createdAt, string status, int attempts) => new()
    {
        // Default kolom baru: baris lama masuk sebagai incomplete (tidak terbukti lengkap) sebelum backfill.
        TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = s.ModelId,
        Status = status, HttpStatus = 200, CreatedAt = createdAt, Attempts = attempts,
    };

    private static UsageDaily LegacyDaily(Scenario s, DateTime day, long requests, long denied) => new()
    {
        TenantId = s.TenantId, ProjectId = s.ProjectId, ApiKeyId = s.KeyId, ModelId = s.ModelId,
        Day = DateOnly.FromDateTime(day), Requests = requests, Denied = denied,
    };

    [Fact]
    public async Task Migration_backfill_marks_legacy_rows_and_partial_retention_conservatively()
    {
        var s = await SetupAsync();
        Db.InstallTenantSecurity(); // backfill must work with fail-closed RLS, not only an unprotected lab.
        var partialDay = DateTime.UtcNow.Date.AddDays(-10); // log sebagian sudah dihapus retensi
        var purgedDay = DateTime.UtcNow.Date.AddDays(-20);  // semua log hari itu sudah dihapus
        var noAttemptDay = DateTime.UtcNow.Date.AddDays(-15); // log tersisa hanya tanpa percobaan provider

        await WithServicesAsync(s.TenantId, async (_, ctx) =>
        {
            ctx.UsageLogs.AddRange(
                Legacy(s, partialDay.AddHours(1), UsageStatuses.Ok, attempts: 1),
                Legacy(s, partialDay.AddHours(2), UsageStatuses.Ok, attempts: 1),
                Legacy(s, partialDay.AddHours(3), UsageStatuses.Denied, attempts: 0),
                Legacy(s, noAttemptDay.AddHours(1), UsageStatuses.Error, attempts: 0),
                Legacy(s, noAttemptDay.AddHours(2), UsageStatuses.Error, attempts: 0));
            ctx.UsageDailies.AddRange(
                LegacyDaily(s, partialDay, requests: 10, denied: 2),   // 8 forwarded, log tersisa 2 → 2 + 6
                LegacyDaily(s, noAttemptDay, requests: 3, denied: 1),  // 2 forwarded, log tersisa 2 tanpa percobaan → 0
                LegacyDaily(s, purgedDay, requests: 4, denied: 1));    // tanpa log tersisa → 3 konservatif
            await ctx.SaveChangesAsync();
            return 0;
        });

        await using var raw = Db.NewContext();
        foreach (var statement in new BackfillProbe().Statements()) await raw.Database.ExecuteSqlRawAsync(statement);

        await using var check = Db.NewContext(s.TenantId);
        var logs = await check.UsageLogs.AsNoTracking().ToListAsync();
        Assert.Equal(5, logs.Count);
        // Hanya request forwarded yang bertahan sebagai batas bawah; denied dan percobaan tanpa provider lengkap.
        Assert.Equal(2, logs.Count(l => l.UsageIncomplete));
        Assert.All(logs.Where(l => l.Status != UsageStatuses.Ok), l => Assert.False(l.UsageIncomplete));

        var days = await check.UsageDailies.AsNoTracking().ToListAsync();
        Assert.Equal(8L, days.Single(u => u.Day == DateOnly.FromDateTime(partialDay)).IncompleteUsageRequests);
        Assert.Equal(0L, days.Single(u => u.Day == DateOnly.FromDateTime(noAttemptDay)).IncompleteUsageRequests);
        Assert.Equal(3L, days.Single(u => u.Day == DateOnly.FromDateTime(purgedDay)).IncompleteUsageRequests);
    }
}
