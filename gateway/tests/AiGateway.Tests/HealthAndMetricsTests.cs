using System.Diagnostics.Metrics;
using System.Net;
using System.Text.RegularExpressions;
using AiGateway.Api.Http;
using AiGateway.Core.Maintenance;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

/// <summary>
/// GB-05, unit: <see cref="MetricsCollector"/> hanya meneruskan meter dan label yang di-allowlist, membatasi jumlah seri dan
/// panjang nilai label, dan menghasilkan teks Prometheus yang benar. Instrumen uji memakai nama unik supaya tidak bercampur dengan
/// pengukuran test lain yang berjalan paralel di proses yang sama.
/// </summary>
public class MetricsCollectorTests
{
    private static readonly Meter AllowedMeter = new("AiGateway.Http", "1.0");
    private static readonly Meter OtherMeter = new("Some.Other.Meter", "1.0");

    [Fact]
    public void Counters_render_in_prometheus_text_and_drop_labels_outside_the_allowlist()
    {
        using var collector = new MetricsCollector();
        var counter = AllowedMeter.CreateCounter<long>("gateway.test.unit.requests");
        counter.Add(2, new KeyValuePair<string, object?>("area", "data"), new("status", 200), new("tenant", "acme"), new("model", "gpt-x"), new("api_key", "gw_secret"));
        counter.Add(3, new KeyValuePair<string, object?>("area", "data"), new("status", 200), new("tenant", "other"));
        counter.Add(1, new KeyValuePair<string, object?>("area", "web"), new("status", 404));

        var text = collector.Render();
        Assert.Contains("# TYPE gateway_test_unit_requests_total counter\n", text);
        Assert.Contains("gateway_test_unit_requests_total{area=\"data\",status=\"200\"} 5\n", text); // label tenant/model/key dibuang, seri digabung
        Assert.Contains("gateway_test_unit_requests_total{area=\"web\",status=\"404\"} 1\n", text);
        Assert.DoesNotContain("tenant", text);
        Assert.DoesNotContain("acme", text);
        Assert.DoesNotContain("gw_secret", text);
        Assert.DoesNotContain("api_key", text);
    }

    [Fact]
    public void Histograms_render_cumulative_buckets_sum_and_count()
    {
        using var collector = new MetricsCollector();
        var histogram = AllowedMeter.CreateHistogram<double>("gateway.test.unit.duration", "ms");
        foreach (var value in new[] { 3.0, 7.0, 7.0, 40.0, 20000.0 })
            histogram.Record(value, new KeyValuePair<string, object?>("area", "data"));

        var text = collector.Render();
        Assert.Contains("# TYPE gateway_test_unit_duration_milliseconds histogram\n", text);
        Assert.Contains("gateway_test_unit_duration_milliseconds_bucket{area=\"data\",le=\"5\"} 1\n", text);
        Assert.Contains("gateway_test_unit_duration_milliseconds_bucket{area=\"data\",le=\"10\"} 3\n", text);
        Assert.Contains("gateway_test_unit_duration_milliseconds_bucket{area=\"data\",le=\"50\"} 4\n", text);
        Assert.Contains("gateway_test_unit_duration_milliseconds_bucket{area=\"data\",le=\"10000\"} 4\n", text);
        Assert.Contains("gateway_test_unit_duration_milliseconds_bucket{area=\"data\",le=\"+Inf\"} 5\n", text);
        Assert.Contains("gateway_test_unit_duration_milliseconds_count{area=\"data\"} 5\n", text);
        Assert.Contains("gateway_test_unit_duration_milliseconds_sum{area=\"data\"} 20057\n", text);
    }

    [Fact]
    public void Only_allowlisted_meters_are_collected()
    {
        using var collector = new MetricsCollector();
        OtherMeter.CreateCounter<long>("gateway.test.unit.foreign").Add(9, new KeyValuePair<string, object?>("area", "data"));

        Assert.DoesNotContain("gateway_test_unit_foreign", collector.Render());
    }

    [Fact]
    public void The_number_of_series_per_metric_and_label_length_are_bounded()
    {
        using var collector = new MetricsCollector();
        var counter = AllowedMeter.CreateCounter<long>("gateway.test.unit.cardinality");
        counter.Add(1, new KeyValuePair<string, object?>("provider", new string('x', 500))); // seri pertama; nilai dipangkas
        for (var i = 0; i < MetricsCollector.MaxSeriesPerMetric + 50; i++)
            counter.Add(1, new KeyValuePair<string, object?>("status", i));

        var text = collector.Render();
        var lines = text.Split('\n').Count(l => l.StartsWith("gateway_test_unit_cardinality_total{", StringComparison.Ordinal));
        Assert.Equal(MetricsCollector.MaxSeriesPerMetric, lines); // seri berlebih tidak membesarkan keluaran
        Assert.Contains("gateway_metrics_dropped_total 51\n", text); // 250 seri status diminta, hanya 199 yang muat
        Assert.Contains($"provider=\"{new string('x', MetricsCollector.MaxLabelValueLength)}\"", text);
        Assert.DoesNotContain(new string('x', MetricsCollector.MaxLabelValueLength + 1), text);
    }

    [Fact]
    public void Options_require_a_strong_token_and_a_safe_path_when_enabled()
    {
        Assert.Null(new MetricsOptions().Validate()); // nonaktif: tanpa syarat
        Assert.NotNull(new MetricsOptions { Enabled = true }.Validate());
        Assert.NotNull(new MetricsOptions { Enabled = true, BearerToken = "terlalu-pendek" }.Validate());
        Assert.Null(new MetricsOptions { Enabled = true, BearerToken = new string('t', 24) }.Validate());
        foreach (var path in new[] { "metrics", "/", "/v1/metrics", "/admin/metrics", "/client/api/x", "/platform", "/healthz", "/healthz/metrics" })
            Assert.NotNull(new MetricsOptions { Enabled = true, BearerToken = new string('t', 24), Path = path }.Validate());
    }
}

/// <summary>GB-05, lewat host sungguhan: liveness, readiness (database, migration, worker maintenance), dan endpoint metrik opsional.</summary>
public class HealthAndMetricsTests(TestDb db) : GatewayTestBase(db)
{
    private const string Token = "metrics-token-0123456789-abcdef";

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Check(string body, string name) =>
        System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("checks").GetProperty(name).GetString()!;

    // --- liveness dan readiness -----------------------------------------------------------

    [Fact]
    public async Task Liveness_is_ok_and_readiness_reports_every_check()
    {
        var live = await GetAsync(Client, "/healthz/live");
        Assert.Equal(HttpStatusCode.OK, live.Status);
        Assert.Contains("\"live\"", live.Body);

        var ready = await GetAsync(Client, "/healthz/ready");
        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.Equal(("ok", "ok", "ok"), (Check(ready.Body, "database"), Check(ready.Body, "migrations"), Check(ready.Body, "maintenance")));

        // Endpoint lama tetap seperti sebelumnya: ping database.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Client, "/healthz")).Status);
    }

    [Fact]
    public async Task Readiness_turns_503_when_the_maintenance_worker_goes_stale_and_recovers_after_a_successful_run()
    {
        var clock = new CooldownClock(DateTimeOffset.UtcNow);
        using var factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Maintenance:CycleSeconds", "1440"); // siklus nyata tidak akan jalan sendiri selama test
            b.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/healthz/ready")).Status); // masa tenggang sejak start

        var worker = factory.Services.GetRequiredService<MaintenanceWorker>();
        await worker.RunOnceAsync(default);
        clock.Now += TimeSpan.FromHours(2); // jauh melewati batas (3 siklus = 72 menit)
        var stale = await GetAsync(client, "/healthz/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stale.Status);
        Assert.Equal(("ok", "ok", "stale"), (Check(stale.Body, "database"), Check(stale.Body, "migrations"), Check(stale.Body, "maintenance")));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/healthz/live")).Status); // liveness tidak ikut gagal

        await worker.RunOnceAsync(default); // job sukses memperbarui heartbeat
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/healthz/ready")).Status);
    }

    [Fact]
    public async Task Readiness_is_503_without_leaking_details_while_the_database_is_offline()
    {
        var builder = new SqlConnectionStringBuilder(Db.ConnectionString);
        var name = builder.InitialCatalog;
        var master = new SqlConnectionStringBuilder(Db.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        async Task SetStateAsync(string state)
        {
            await using var connection = new SqlConnection(master);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{name}] SET {state} WITH ROLLBACK IMMEDIATE";
            await command.ExecuteNonQueryAsync();
        }

        await SetStateAsync("OFFLINE");
        try
        {
            var offline = await GetAsync(Client, "/healthz/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.Status);
            Assert.Equal(("fail", "skipped"), (Check(offline.Body, "database"), Check(offline.Body, "migrations")));
            Assert.DoesNotContain(name, offline.Body); // tanpa nama database, server, atau pesan galat
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(Client, "/healthz/live")).Status);
        }
        finally
        {
            await SetStateAsync("ONLINE");
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode last;
        do
        {
            last = (await GetAsync(Client, "/healthz/ready")).Status;
            if (last == HttpStatusCode.OK) break;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Equal(HttpStatusCode.OK, last); // pulih sendiri begitu database kembali
    }

    // --- metrik ---------------------------------------------------------------------------

    [Fact]
    public async Task Metrics_are_not_served_by_default()
    {
        var response = await GetAsync(Client, "/metrics", Token);
        Assert.DoesNotContain("# TYPE gateway_", response.Body);
    }

    [Fact]
    public async Task Enabled_metrics_require_the_bearer_token_and_expose_only_bounded_labels()
    {
        using var factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Metrics:Enabled", "true");
            b.UseSetting("Metrics:BearerToken", Token);
        });
        using var client = factory.CreateClient();
        await GetAsync(client, "/healthz");
        var s = await SetupAsync();
        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(s.ApiKey, Chat())).StatusCode); // area data
        await factory.Services.GetRequiredService<MaintenanceWorker>().RunOnceAsync(default);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/metrics")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/metrics", "token-yang-salah")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/metrics", Token[..^1])).Status); // awalan token tidak cukup

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new("Bearer", Token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; version=0.0.4; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("# TYPE gateway_http_requests_total counter", body);
        Assert.Contains("gateway_http_requests_total{area=\"web\",status=\"200\"}", body);
        Assert.Contains("gateway_http_requests_total{area=\"data\",status=\"200\"}", body);
        Assert.Contains("# TYPE gateway_http_duration_milliseconds histogram", body);
        Assert.Contains("gateway_http_duration_milliseconds_bucket{area=\"data\",le=\"+Inf\"}", body);
        Assert.Contains("gateway_maintenance_last_success_timestamp_seconds{job=\"maintenance.webhook_delivery\"}", body);
        Assert.Contains("gateway_uptime_seconds", body);

        // Tidak ada label tenant/key/model/path: hanya kunci label yang di-allowlist (plus le dan job).
        var keys = Regex.Matches(body, @"[{,](\w+)=""").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Subset(new HashSet<string> { "area", "status", "operation", "provider", "le", "job" }, new HashSet<string>(keys));
        Assert.DoesNotContain(s.ApiKey, body);
        Assert.DoesNotContain(UpstreamKey, body);
    }

    [Fact]
    public void Enabling_metrics_without_a_token_or_with_an_unsafe_path_fails_at_startup()
    {
        using var noToken = Factory.WithWebHostBuilder(b => b.UseSetting("Metrics:Enabled", "true"));
        var ex = Assert.ThrowsAny<Exception>(() => noToken.CreateClient());
        Assert.Contains("Metrics", ex.ToString());

        using var badPath = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Metrics:Enabled", "true");
            b.UseSetting("Metrics:BearerToken", Token);
            b.UseSetting("Metrics:Path", "/admin/api/metrics");
        });
        Assert.ThrowsAny<Exception>(() => badPath.CreateClient());
    }
}
