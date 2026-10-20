using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

public class GatewaySettingsProviderTests
{
    internal const string AdminInstructions = "INSTRUKSI-ADMIN-RAHASIA";
    internal const string RemotePrompt = "INSTRUKSI-GATEWAY-BARU";

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    internal static HttpResponseMessage Json(string items, string version = "v1") => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"version\":\"" + version + "\",\"items\":" + items + "}", Encoding.UTF8, "application/json"),
    };

    /// <summary>Konfigurasi lokal lengkap seperti appsettings API; sumber belakangan menang (override lokal).</summary>
    internal static ConfigurationManager LocalConfiguration()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operator:MaxToolLoops"] = "5",
            ["Operator:MaxHistoryMessages"] = "40",
            ["Operator:MaxTokensPerConversation"] = "200000",
            ["Queue:MaxRetries"] = "3",
            ["Queue:StaleLockMinutes"] = "10",
            ["Ai:MaxTokens"] = "4096",
            ["Response:Detail"] = "mendalam",
            ["Prompt:AdditionalInstructions"] = AdminInstructions,
            ["Dispatcher:MaxConcurrentOperators"] = "4",
            ["Dispatcher:MaxConcurrentSpecialists"] = "2",
            ["Dispatcher:SweepSeconds"] = "5",
            ["Dispatcher:OperatorDll"] = "operator.dll",
            ["Dispatcher:SpecialistDll"] = "specialist.dll",
            ["Internal:Key"] = "local-secret",
        });
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Dispatcher:MaxConcurrentOperators"] = "3" });
        return config;
    }

    internal static GatewaySettingsProvider Provider(ConfigurationManager config)
    {
        var provider = new GatewaySettingsProvider(config);
        ((IConfigurationBuilder)config).Add(provider);
        return provider;
    }

    internal static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://gateway.test/v1/") };

    internal static QueueRuntimeSnapshot NoWork() =>
        new(new StatusCounts(0, 0, 0, 0), new StatusCounts(0, 0, 0, 0), []);

    private const string AllControls = """
        [{"key":"Operator:MaxToolLoops","value":"2","valueType":"int"},
         {"key":"Operator:MaxHistoryMessages","value":"10","valueType":"int"},
         {"key":"Operator:MaxTokensPerConversation","value":"5000","valueType":"int"},
         {"key":"Queue:MaxRetries","value":"4","valueType":"int"},
         {"key":"Queue:StaleLockMinutes","value":"30","valueType":"int"},
         {"key":"Ai:MaxTokens","value":"800","valueType":"int"},
        {"key":"Response:Detail","value":"ringkas","valueType":"text"},
        {"key":"Operator:FileTools","value":"selalu","valueType":"text"},
        {"key":"Dispatcher:MaxConcurrentOperators","value":"1","valueType":"int"},
        {"key":"Dispatcher:MaxConcurrentSpecialists","value":"16","valueType":"int"},
        {"key":"Dispatcher:SweepSeconds","value":"300","valueType":"int"},
        {"key":"Dispatcher:OperatorDll","value":"gateway-operator.dll","valueType":"text"},
        {"key":"Prompt:AdditionalInstructions","value":"INSTRUKSI-GATEWAY-BARU","valueType":"text"},
        {"key":"Internal:Key","value":"remote-secret","valueType":"text"},
        {"key":"Dispatcher:Unknown","value":"9","valueType":"int"}]
        """;

    [Fact]
    public void Local_baseline_is_the_initial_policy_and_reports_local()
    {
        var provider = Provider(LocalConfiguration());

        var policy = provider.CurrentPolicy;
        Assert.Null(policy.SourceSettingsVersion);
        Assert.Equal((5, 40, 200000, 3, 10, 4096, "mendalam"),
            (policy.MaxToolLoops, policy.MaxHistoryMessages, policy.MaxTokensPerConversation,
                policy.MaxRetries, policy.StaleLockMinutes, policy.MaxOutputTokens, policy.Detail));
        Assert.Equal(AdminInstructions, policy.AdditionalInstructions);
        Assert.Equal("auto", policy.FileTools);

        var report = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.Equal("local", report.SyncStatus);
        Assert.Null(report.LastConfigSuccessAt);
        Assert.Equal(policy.PolicyHash, report.PolicyHash);
        Assert.Equal(policy.PromptHash, report.PromptHash);
        Assert.Equal("200000", report.EffectiveValues["Operator:MaxTokensPerConversation"]);
    }

    [Fact]
    public async Task Accepted_snapshot_updates_options_and_policy_then_deletion_restores_local()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        var services = new ServiceCollection().AddDispatcherOptions(config);
        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<DispatcherOptions>>();
        Assert.Equal(3, monitor.CurrentValue.MaxConcurrentOperators);

        using var http = Client(new Handler(_ => Json(AllControls)));
        Assert.True(await provider.RefreshAsync(http, CancellationToken.None));

        // Kontrol allowlist menimpa IOptionsMonitor dispatcher termasuk path DLL (semantik lama); key lain tidak.
        Assert.Equal((1, 16, 300, "gateway-operator.dll"), (monitor.CurrentValue.MaxConcurrentOperators,
            monitor.CurrentValue.MaxConcurrentSpecialists, monitor.CurrentValue.SweepSeconds, monitor.CurrentValue.OperatorDll));
        Assert.Equal("local-secret", config["Internal:Key"]);
        Assert.Null(config["Dispatcher:Unknown"]);

        var policy = provider.CurrentPolicy;
        Assert.Equal((2, 10, 5000, 4, 30, 800, "ringkas"),
            (policy.MaxToolLoops, policy.MaxHistoryMessages, policy.MaxTokensPerConversation,
                policy.MaxRetries, policy.StaleLockMinutes, policy.MaxOutputTokens, policy.Detail));
        Assert.Equal("v1", policy.SourceSettingsVersion);
        Assert.Equal(RemotePrompt, policy.AdditionalInstructions);
        Assert.Equal("selalu", policy.FileTools);
        Assert.Equal(RemotePrompt, config["Prompt:AdditionalInstructions"]);

        var synced = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.Equal("synced", synced.SyncStatus);
        Assert.NotNull(synced.LastConfigSuccessAt);
        Assert.Contains("Dispatcher:Unknown", synced.IgnoredKeys);
        Assert.Contains("Internal:Key", synced.IgnoredKeys);
        Assert.DoesNotContain("Dispatcher:OperatorDll", synced.IgnoredKeys);
        Assert.DoesNotContain("Prompt:AdditionalInstructions", synced.IgnoredKeys);
        Assert.DoesNotContain("Prompt:AdditionalInstructions", synced.EffectiveValues.Keys);
        var wire = JsonSerializer.Serialize(synced, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(RemotePrompt, wire);
        Assert.DoesNotContain("gateway-operator.dll", wire);

        // Penghapusan nilai gateway mengembalikan nilai lokal yang dikunci di baseline, bukan nilai lain.
        using var empty = Client(new Handler(_ => Json("[]", "v2")));
        Assert.True(await provider.RefreshAsync(empty, CancellationToken.None));
        Assert.Equal((3, 2, 5, "operator.dll"), (monitor.CurrentValue.MaxConcurrentOperators,
            monitor.CurrentValue.MaxConcurrentSpecialists, monitor.CurrentValue.SweepSeconds, monitor.CurrentValue.OperatorDll));
        var restored = provider.CurrentPolicy;
        Assert.Equal((5, 40, 200000, 3, 10, 4096, "mendalam"),
            (restored.MaxToolLoops, restored.MaxHistoryMessages, restored.MaxTokensPerConversation,
                restored.MaxRetries, restored.StaleLockMinutes, restored.MaxOutputTokens, restored.Detail));
        Assert.Equal("v2", restored.SourceSettingsVersion);
        Assert.Equal(AdminInstructions, restored.AdditionalInstructions);
        Assert.Equal(AdminInstructions, config["Prompt:AdditionalInstructions"]);
        // Kebijakan yang sudah dipegang pesan lama tetap memakai instruksi lama: snapshot tidak berubah retroaktif.
        Assert.Equal(RemotePrompt, policy.AdditionalInstructions);
    }

    [Fact]
    public async Task Runtime_policy_replaces_limits_atomically_and_retains_the_prior_workflow_snapshot()
    {
        var provider = Provider(LocalConfiguration());
        var original = provider.CurrentPolicy;
        using var lowered = Client(new Handler(_ => Json("""
            [{"key":"Ai:AttemptTimeoutSeconds","value":"20","valueType":"int"},
             {"key":"Ai:TotalRequestTimeoutSeconds","value":"40","valueType":"int"},
             {"key":"Ai:StreamFirstByteSeconds","value":"10","valueType":"int"},
             {"key":"Upload:MaxBytes","value":"65536","valueType":"int"},
             {"key":"Upload:MaxAttachmentsPerMessage","value":"2","valueType":"int"},
             {"key":"Upload:AllowedExtensions","value":" .PDF, .png ","valueType":"text"},
             {"key":"Document:MaxPages","value":"2","valueType":"int"},
             {"key":"Document:WatchdogSeconds","value":"3","valueType":"int"}]
            """, "limits-a")));
        await provider.RefreshAsync(lowered, CancellationToken.None);
        var accepted = provider.CurrentPolicy;
        Assert.Equal(20, accepted.Ai.AttemptTimeoutSeconds);
        Assert.Equal(65536, accepted.Upload.MaxBytes);
        Assert.Equal(new[] { ".pdf", ".png" }, accepted.Upload.AllowedExtensions);
        Assert.Equal(2, accepted.Document.MaxPages);
        Assert.Equal(3, accepted.Document.WatchdogSeconds);
        Assert.Equal(60, original.Ai.AttemptTimeoutSeconds);
        Assert.Equal(100, original.Document.MaxPages);

        using var invalid = Client(new Handler(_ => Json("""
            [{"key":"Upload:MaxBytes","value":"1024","valueType":"int"},
             {"key":"Ai:TotalRequestTimeoutSeconds","value":"5","valueType":"int"}]
            """, "limits-b")));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.RefreshAsync(invalid, CancellationToken.None));
        Assert.Same(accepted, provider.CurrentPolicy);
        Assert.Equal("rejected", provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow).SyncStatus);

        using var deleted = Client(new Handler(_ => Json("[]", "limits-c")));
        await provider.RefreshAsync(deleted, CancellationToken.None);
        Assert.Equal(original.Upload.MaxBytes, provider.CurrentPolicy.Upload.MaxBytes);
        Assert.Equal(2, accepted.Document.MaxPages); // already-enqueued snapshot never follows deletion
    }

    [Theory]
    [InlineData("Upload:AllowedExtensions", ".pdf,.exe", "text")]
    [InlineData("Upload:AllowedExtensions", ".png,.PNG", "text")]
    [InlineData("Upload:MaxBytes", "10485761", "int")]
    [InlineData("Upload:MaxAttachmentsPerMessage", "7", "int")]
    [InlineData("Document:MaxPages", "101", "int")]
    [InlineData("Document:VisionMaxLongEdge", "1281", "int")]
    [InlineData("Document:WatchdogSeconds", "61", "int")]
    public async Task Unsupported_input_or_safety_ceiling_cannot_replace_a_valid_policy(string key, string value, string type)
    {
        var provider = Provider(LocalConfiguration());
        var previous = provider.CurrentPolicy;
        var items = JsonSerializer.Serialize(new[] { new { key, value, valueType = type } });
        using var client = Client(new Handler(_ => Json(items)));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.RefreshAsync(client, CancellationToken.None));
        Assert.Same(previous, provider.CurrentPolicy);
    }

    [Fact]
    public async Task Local_format_subset_is_not_widened_by_option_defaults_and_is_restored_when_override_is_deleted()
    {
        var configuration = LocalConfiguration();
        configuration["Upload:AllowedExtensions:0"] = ".png";
        var provider = Provider(configuration);
        Assert.Equal(new[] { ".png" }, provider.CurrentPolicy.Upload.AllowedExtensions);
        using var overrideClient = Client(new Handler(_ => Json("""
            [{"key":"Upload:AllowedExtensions","value":".pdf","valueType":"text"}]
            """)));
        await provider.RefreshAsync(overrideClient, CancellationToken.None);
        Assert.Equal(new[] { ".pdf" }, provider.CurrentPolicy.Upload.AllowedExtensions);
        using var deleted = Client(new Handler(_ => Json("[]", "deleted")));
        await provider.RefreshAsync(deleted, CancellationToken.None);
        Assert.Equal(new[] { ".png" }, provider.CurrentPolicy.Upload.AllowedExtensions);
    }

    [Fact]
    public async Task Output_budget_is_optional_and_the_gateway_can_set_then_clear_it()
    {
        var config = LocalConfiguration();
        config["Ai:MaxTokens"] = null; // appsettings tanpa Ai:MaxTokens: batas keluaran diserahkan ke gateway
        var provider = Provider(config);

        Assert.Null(provider.CurrentPolicy.MaxOutputTokens);
        var local = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.False(local.EffectiveValues.ContainsKey("Ai:MaxTokens"));   // tidak dilaporkan sebagai nol palsu

        using var set = Client(new Handler(_ => Json("""[{"key":"Ai:MaxTokens","value":"1500","valueType":"int"}]""")));
        Assert.True(await provider.RefreshAsync(set, CancellationToken.None));
        var withLimit = provider.CurrentPolicy;
        Assert.Equal(1500, withLimit.MaxOutputTokens);
        Assert.Equal("1500", provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow).EffectiveValues["Ai:MaxTokens"]);

        // Hapus di gateway -> kembali ke baseline lokal (kosong); snapshot pesan lama tetap 1500.
        using var cleared = Client(new Handler(_ => Json("[]", "v2")));
        Assert.True(await provider.RefreshAsync(cleared, CancellationToken.None));
        Assert.Null(provider.CurrentPolicy.MaxOutputTokens);
        Assert.Equal(1500, withLimit.MaxOutputTokens);

        // Snapshot tanpa batas lolos round-trip tiket (JSON + hash) dan hash-nya berbeda dari yang berbatas.
        var roundTripped = ExecutionPolicy.FromJson(provider.CurrentPolicy.ToJson());
        Assert.Null(roundTripped.MaxOutputTokens);
        Assert.NotEqual(withLimit.PolicyHash, roundTripped.PolicyHash);
    }

    [Fact]
    public async Task Not_modified_uses_etag_and_keeps_the_last_snapshot()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        var first = true;
        using var http = Client(new Handler(request =>
        {
            if (first)
            {
                first = false;
                var response = Json("""[{"key":"Dispatcher:MaxConcurrentOperators","value":"32","valueType":"int"}]""", "gateway-version");
                response.Headers.ETag = new("\"gateway-version\"");
                return response;
            }
            Assert.Equal("/client/api/settings?namespace=chatbot", request.RequestUri!.PathAndQuery);
            Assert.Equal("\"gateway-version\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return new(HttpStatusCode.NotModified);
        }));

        await provider.RefreshAsync(http, CancellationToken.None);
        var applied = provider.CurrentPolicy;
        Assert.False(await provider.RefreshAsync(http, CancellationToken.None));

        Assert.Equal("32", config["Dispatcher:MaxConcurrentOperators"]);
        Assert.Equal(applied.PolicyHash, provider.CurrentPolicy.PolicyHash);
        Assert.Equal("synced", provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow).SyncStatus);
    }

    [Theory]
    [InlineData("Operator:MaxToolLoops", "21", "int")]
    [InlineData("Operator:MaxToolLoops", "7", "text")]
    [InlineData("Operator:MaxHistoryMessages", "0", "int")]
    [InlineData("Operator:MaxTokensPerConversation", "999", "int")]
    [InlineData("Queue:MaxRetries", "21", "int")]
    [InlineData("Queue:StaleLockMinutes", "1441", "int")]
    [InlineData("Ai:MaxTokens", "0", "int")]
    [InlineData("Response:Detail", "panjang", "text")]
    [InlineData("Operator:FileTools", "nanti", "text")]
    [InlineData("Dispatcher:MaxConcurrentOperators", "1.5", "int")]
    [InlineData("Dispatcher:MaxConcurrentOperators", "2", "text")]
    [InlineData("Dispatcher:MaxConcurrentSpecialists", "17", "int")]
    [InlineData("Dispatcher:SweepSeconds", "301", "int")]
    [InlineData("Dispatcher:OperatorDll", " ", "text")]
    [InlineData("Dispatcher:SpecialistDll", "x.dll", "int")]
    [InlineData("Prompt:AdditionalInstructions", "7", "int")]
    public async Task Invalid_known_value_rejects_whole_candidate_and_keeps_applied_state(string key, string value, string type)
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        var call = 0;
        var bad = $$"""[{"key":"{{key}}","value":"{{value}}","valueType":"{{type}}"},{"key":"Queue:MaxRetries","value":"5","valueType":"int"}]""";
        using var http = Client(new Handler(request =>
        {
            if (call++ == 0) return Json("""[{"key":"Queue:MaxRetries","value":"5","valueType":"int"}]""", "good");
            Assert.Equal("\"good\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return Json(bad, "bad");
        }));

        Assert.True(await provider.RefreshAsync(http, CancellationToken.None));
        var applied = provider.CurrentPolicy;

        for (var i = 0; i < 2; i++)
        {
            var ex = await Record.ExceptionAsync(() => provider.RefreshAsync(http, CancellationToken.None));
            Assert.IsAssignableFrom<InvalidDataException>(ex);
            // Satu nilai salah menolak seluruh kandidat: kebijakan dan konfigurasi tidak bergeser sama sekali.
            Assert.Same(applied, provider.CurrentPolicy);
            Assert.Equal("5", config["Queue:MaxRetries"]);
        }

        var report = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.Equal("rejected", report.SyncStatus);
        Assert.Contains(key, report.RejectedKeys);
        Assert.NotNull(report.LastConfigSuccessAt);
        Assert.True(report.Reason!.Length <= 200);
        Assert.DoesNotContain(AdminInstructions, report.Reason);
    }

    [Fact]
    public async Task Remote_admin_prompt_applies_and_invalid_prompt_keeps_policy_hash_and_etag()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        var local = provider.CurrentPolicy;
        var tooLong = new string('x', 2001);
        var call = 0;
        using var http = Client(new Handler(request =>
        {
            call++;
            // Kandidat yang ditolak tidak boleh memindahkan ETag: percobaan berikutnya masih memakai versi terakhir yang diterima.
            if (call > 1) Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            var response = call switch
            {
                1 => Json($$"""[{"key":"Prompt:AdditionalInstructions","value":"{{RemotePrompt}}","valueType":"text"}]""", "v1"),
                2 => Json($$"""[{"key":"Prompt:AdditionalInstructions","value":"{{tooLong}}","valueType":"text"}]""", "v2"),
                3 => Json("""[{"key":"Prompt:AdditionalInstructions","value":"7","valueType":"int"}]""", "v3"),
                _ => Json("""[{"key":"Prompt:AdditionalInstructions","value":"","valueType":"text"}]""", "v4"),
            };
            response.Headers.ETag = new(call == 1 ? "\"v1\"" : "\"evil\"");
            return response;
        }));

        Assert.True(await provider.RefreshAsync(http, CancellationToken.None));
        var applied = provider.CurrentPolicy;
        Assert.Equal(RemotePrompt, applied.AdditionalInstructions);
        Assert.Equal(local.PromptHash, applied.PromptHash); // hash prompt tetap template inti, bukan teks admin
        Assert.NotEqual(local.PolicyHash, applied.PolicyHash);
        Assert.Equal(RemotePrompt, config["Prompt:AdditionalInstructions"]);

        foreach (var _ in new[] { 0, 1 })
        {
            var ex = await Record.ExceptionAsync(() => provider.RefreshAsync(http, CancellationToken.None));
            Assert.IsAssignableFrom<InvalidDataException>(ex);
            // Prompt terlalu panjang / bertipe salah menolak seluruh kandidat: kebijakan, hash, dan konfigurasi tetap.
            Assert.Same(applied, provider.CurrentPolicy);
            Assert.Equal(RemotePrompt, config["Prompt:AdditionalInstructions"]);
        }

        var rejected = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.Equal("rejected", rejected.SyncStatus);
        Assert.Contains("Prompt:AdditionalInstructions", rejected.RejectedKeys);
        var wire = JsonSerializer.Serialize(rejected, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(tooLong, wire);       // nilai kandidat yang ditolak tidak bocor ke laporan
        Assert.DoesNotContain(RemotePrompt, wire);  // nilai prompt yang berlaku pun tidak pernah dilaporkan
        Assert.DoesNotContain("Prompt:AdditionalInstructions", rejected.EffectiveValues.Keys);

        // Prompt kosong sah seperti PromptOptions bawaan.
        Assert.True(await provider.RefreshAsync(http, CancellationToken.None));
        Assert.Equal("", provider.CurrentPolicy.AdditionalInstructions);
        Assert.Equal("", config["Prompt:AdditionalInstructions"]);
    }

    [Fact]
    public async Task Unknown_keys_are_ignored_and_only_bounded_safe_names_are_reported()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        var items = new List<object>(Enumerable.Range(0, 40)
            .Select(i => new { key = $"Vendor:Setting{i}", value = "x", valueType = "text" }));
        items.Add(new { key = "bad key!", value = "x", valueType = "text" });
        items.Add(new { key = new string('a', 120), value = "x", valueType = "text" });
        items.Add(new { key = ":leading", value = "x", valueType = "text" });
        var body = new StringContent(JsonSerializer.Serialize(new { version = "v1", items }), Encoding.UTF8, "application/json");
        using var http = Client(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));

        await provider.RefreshAsync(http, CancellationToken.None);

        // Key asing tidak pernah menimpa apa pun, dan nama yang tidak aman tidak dikirim ke gateway.
        Assert.Equal((5, 200000, "mendalam"),
            (provider.CurrentPolicy.MaxToolLoops, provider.CurrentPolicy.MaxTokensPerConversation, provider.CurrentPolicy.Detail));
        var report = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.Equal("synced", report.SyncStatus);
        Assert.Equal(32, report.IgnoredKeys.Count);
        Assert.Contains("Vendor:Setting0", report.IgnoredKeys);
        Assert.DoesNotContain(report.IgnoredKeys, k => k.Contains(' '));
        Assert.DoesNotContain(new string('a', 120), report.IgnoredKeys);
    }

    [Fact]
    public async Task Case_variant_known_key_is_canonicalized()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        using var http = Client(new Handler(_ =>
            Json("""[{"key":"queue:maxretries","value":"5","valueType":"int"}]""")));

        await provider.RefreshAsync(http, CancellationToken.None);

        Assert.Equal(5, provider.CurrentPolicy.MaxRetries);
        Assert.Equal("5", config["Queue:MaxRetries"]);
        Assert.Equal("5", provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow).EffectiveValues["Queue:MaxRetries"]);
    }

    [Theory]
    [InlineData("""[{"key":"queue:maxretries","value":"5","valueType":"int"},{"key":"Queue:MaxRetries","value":"6","valueType":"int"}]""")]
    [InlineData("""[{"key":"Queue:MaxRetries","value":"5","valueType":"int"},{"key":"Queue:MaxRetries","value":"6","valueType":"int"}]""")]
    public async Task Duplicate_key_in_any_case_rejects_whole_candidate(string items)
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        using var http = Client(new Handler(_ => Json(items)));

        var ex = await Record.ExceptionAsync(() => provider.RefreshAsync(http, CancellationToken.None));

        Assert.IsAssignableFrom<InvalidDataException>(ex);
        Assert.Equal(3, provider.CurrentPolicy.MaxRetries);
        Assert.Equal("3", config["Queue:MaxRetries"]);
        Assert.Contains("Queue:MaxRetries", provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow).RejectedKeys);
    }

    [Fact]
    public async Task Network_or_auth_failure_preserves_values_and_marks_last_known_good()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        var call = 0;
        using var http = Client(new Handler(_ => ++call switch
        {
            1 => throw new HttpRequestException("offline"),
            2 => Json("""[{"key":"Dispatcher:MaxConcurrentOperators","value":"1","valueType":"int"}]"""),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RefreshAsync(http, CancellationToken.None));
        var before = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.Equal("local", before.SyncStatus);
        Assert.Null(before.LastConfigSuccessAt);
        Assert.Equal("3", config["Dispatcher:MaxConcurrentOperators"]);

        await provider.RefreshAsync(http, CancellationToken.None);
        var applied = provider.CurrentPolicy;
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RefreshAsync(http, CancellationToken.None));

        var failed = provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow);
        Assert.Equal("last-known-good", failed.SyncStatus);
        Assert.NotNull(failed.LastConfigSuccessAt);
        Assert.False(string.IsNullOrEmpty(failed.Reason));
        Assert.Same(applied, provider.CurrentPolicy);
        Assert.Equal("1", config["Dispatcher:MaxConcurrentOperators"]);
    }

    [Fact]
    public async Task Same_values_with_new_version_keep_configuration_and_track_applied_version()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        var call = 0;
        using var http = Client(new Handler(_ => ++call == 1
            ? Json("""[{"key":"Queue:MaxRetries","value":"5","valueType":"int"}]""", "v1")
            : Json("""[{"key":"Queue:MaxRetries","value":"5","valueType":"int"}]""", "v2")));

        Assert.True(await provider.RefreshAsync(http, CancellationToken.None));
        Assert.False(await provider.RefreshAsync(http, CancellationToken.None));

        Assert.Equal(5, provider.CurrentPolicy.MaxRetries);
        Assert.Equal("v2", provider.CurrentPolicy.SourceSettingsVersion);
        Assert.Equal("synced", provider.CreateReport(NoWork(), 15, DateTimeOffset.UtcNow).SyncStatus);
    }

    [Theory]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"version\":\"bad\",\"items\":null}")]
    [InlineData("{\"version\":\"bad\",\"items\":[null]}")]
    [InlineData("{\"version\":\"bad\",\"items\":[{\"key\":\"Queue:MaxRetries\"}]}")]
    [InlineData("{\"version\":\"bad name\",\"items\":[{\"key\":\"Queue:MaxRetries\",\"value\":\"5\",\"valueType\":\"int\"}]}")]
    [InlineData("{\"version\":\"bad\",\"items\":[{\"key\":\"Queue:MaxRetries\",\"value\":\"5\",\"valueType\":\"int\"},{\"key\":\"queue:maxretries\",\"value\":\"6\",\"valueType\":\"int\"}]}")]
    public async Task Incomplete_or_duplicate_snapshot_does_not_erase_local_configuration(string body)
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        using var http = Client(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.RefreshAsync(http, CancellationToken.None));

        Assert.Equal("3", config["Queue:MaxRetries"]);
        Assert.Equal(3, provider.CurrentPolicy.MaxRetries);
    }

    [Fact]
    public async Task Report_stays_within_gateway_allowlist_and_never_leaks_prompt_or_secrets()
    {
        var config = LocalConfiguration();
        var provider = Provider(config);
        using var http = Client(new Handler(_ => Json(AllControls)));
        await provider.RefreshAsync(http, CancellationToken.None);

        var runtime = new QueueRuntimeSnapshot(new StatusCounts(7, 3, 11, 2), new StatusCounts(1, 0, 4, 5),
            [new ActivePolicyCount(new string('a', 64), 2, 1)]);
        var observed = DateTimeOffset.UtcNow;
        var report = provider.CreateReport(runtime, 15, observed);

        Assert.Equal("chatbot", report.Namespace);
        Assert.Equal("synced", report.SyncStatus);
        Assert.Equal(provider.CurrentPolicy.PolicyHash, report.PolicyHash);
        Assert.Matches("^[0-9a-f]{64}$", report.PolicyHash);
        Assert.Matches("^[0-9a-f]{64}$", report.PromptHash);
        Assert.Equal("2", report.EffectiveValues["Operator:MaxToolLoops"]);
        Assert.Equal("ringkas", report.EffectiveValues["Response:Detail"]);

        var instructions = report.SupportedKeys.Single(k => k.Key == "Prompt:AdditionalInstructions");
        Assert.True(instructions.Redacted);
        Assert.Equal(2000, instructions.MaxLength);
        var detail = report.SupportedKeys.Single(k => k.Key == "Response:Detail");
        Assert.Equal(new[] { "ringkas", "normal", "mendalam" }, detail.AllowedValues);

        // Hitungan nyata dari repositori, bukan angka karangan.
        Assert.Equal((7L, 3L, 11L, 2L), (report.Queue.Pending, report.Queue.Processing, report.Queue.Done, report.Queue.Failed));
        Assert.Equal((1L, 0L, 4L, 5L), (report.Jobs.Pending, report.Jobs.Processing, report.Jobs.Done, report.Jobs.Failed));
        Assert.Equal(new string('a', 64), report.ActivePolicies.Single().PolicyHash);
        Assert.Equal(15, report.ReportIntervalSeconds);
        Assert.Equal(observed, report.ObservedAt);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(AdminInstructions, json);
        Assert.DoesNotContain(RemotePrompt, json);
        Assert.DoesNotContain("local-secret", json);
        Assert.DoesNotContain("remote-secret", json);
        Assert.DoesNotContain("gateway-operator.dll", json);
        Assert.DoesNotContain("operator.dll", json);

        var many = new QueueRuntimeSnapshot(new StatusCounts(0, 0, 0, 0), new StatusCounts(0, 0, 0, 0),
            [.. Enumerable.Range(0, 200).Select(i => new ActivePolicyCount(i.ToString("x64"), i, i))]);
        // Daftar kepanjangan ditolak utuh (bukan dipotong): poller melewati laporan dan status menjadi stale.
        Assert.Throws<InvalidDataException>(() => provider.CreateReport(many, 15, observed));
    }
}

public class GatewaySettingsPollerTests : QueueTestBase
{
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://gateway.test/v1/") };
    }

    private static GatewaySettingsPoller Poller(GatewaySettingsProvider provider, HttpMessageHandler handler, QueueRepository queue) =>
        new(provider, new Factory(handler), Options.Create(new SettingsOptions { RefreshSeconds = 15 }), queue,
            TimeProvider.System, NullLogger<GatewaySettingsPoller>.Instance);

    [Fact]
    public async Task Poll_refreshes_reports_real_counts_and_survives_report_failure()
    {
        var config = GatewaySettingsProviderTests.LocalConfiguration();
        var provider = GatewaySettingsProviderTests.Provider(config);
        await EnqueueAsync(await NewConversationAsync());

        Uri? post = null;
        string? body = null;
        var handler = new AsyncHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return GatewaySettingsProviderTests.Json("""[{"key":"Operator:MaxToolLoops","value":"2","valueType":"int"}]""");
            post = request.RequestUri;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await Poller(provider, handler, NewRepo()).PollOnceAsync(CancellationToken.None);

        Assert.Equal("/client/api/app-status", post!.PathAndQuery);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("chatbot", doc.RootElement.GetProperty("namespace").GetString());
        Assert.Equal("synced", doc.RootElement.GetProperty("syncStatus").GetString());
        Assert.Equal(1L, doc.RootElement.GetProperty("queue").GetProperty("pending").GetInt64());
        Assert.DoesNotContain(GatewaySettingsProviderTests.AdminInstructions, body);

        // POST gagal (500) tidak boleh mengubah konfigurasi atau kebijakan yang berlaku.
        Assert.Equal("2", config["Operator:MaxToolLoops"]);
        Assert.Equal(2, provider.CurrentPolicy.MaxToolLoops);
        Assert.Equal("synced", provider.CreateReport(GatewaySettingsProviderTests.NoWork(), 15, DateTimeOffset.UtcNow).SyncStatus);
    }

    [Fact]
    public async Task Queue_read_failure_skips_report_instead_of_fabricating_zeros()
    {
        var config = GatewaySettingsProviderTests.LocalConfiguration();
        var provider = GatewaySettingsProviderTests.Provider(config);
        var posts = 0;
        var handler = new AsyncHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(GatewaySettingsProviderTests.Json("""[{"key":"Operator:MaxToolLoops","value":"2","valueType":"int"}]"""));
            posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var broken = new QueueRepository(
            "Server=127.0.0.1,1;Database=none;User Id=sa;Password=x;TrustServerCertificate=True;Connect Timeout=1");

        await Poller(provider, handler, broken).PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, posts);
        Assert.Equal("2", config["Operator:MaxToolLoops"]);
    }
}
