using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Maintenance;

/// <summary>
/// Pekerja periodik G4: retensi (log, body, antrean lama), evaluasi alert kuota, dan pengiriman webhook.
/// Setiap eksekusi dicatat di <c>job_runs</c>. Retensi dan pengiriman bekerja lintas tenant (IgnoreQueryFilters);
/// evaluasi alert menyetel tenant per iterasi.
/// </summary>
public sealed class MaintenanceWorker(
    MaintenanceScopeFactory scopes, IOptions<MaintenanceOptions> options, TimeProvider clock, ILogger<MaintenanceWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.CycleSeconds));
        var nextAlerts = clock.GetUtcNow();
        var nextRetention = clock.GetUtcNow();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = clock.GetUtcNow();
                try
                {
                    await RunDeliveryAsync(stoppingToken);
                    if (now >= nextAlerts)
                    {
                        await RunAlertsAsync(stoppingToken);
                        nextAlerts = now.AddMinutes(o.AlertIntervalMinutes);
                    }
                    if (now >= nextRetention)
                    {
                        await RunRetentionAsync(stoppingToken);
                        nextRetention = now.AddMinutes(o.RetentionIntervalMinutes);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Siklus maintenance gagal");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // penghentian host
        }
    }

    /// <summary>Satu siklus penuh tanpa menunggu jadwal; dipakai test dan job manual.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await RunRetentionAsync(ct);
        await RunAlertsAsync(ct);
        await RunDeliveryAsync(ct);
    }

    private Task RunRetentionAsync(CancellationToken ct) =>
        RunJobAsync("maintenance.retention", ct, async sp =>
        {
            var r = await sp.GetRequiredService<RetentionService>().RunAsync(ct);
            return $"bodies={r.BodiesDeleted};days={r.DaysReconciled};logs={r.LogsDeleted};deliveries={r.DeliveriesDeleted};jobRuns={r.JobRunsDeleted}";
        });

    private Task RunAlertsAsync(CancellationToken ct) =>
        RunJobAsync("maintenance.alerts", ct, async sp =>
            $"events={await sp.GetRequiredService<AlertService>().EvaluateAllAsync(ct)}");

    private Task RunDeliveryAsync(CancellationToken ct) =>
        RunJobAsync("maintenance.webhook_delivery", ct, async sp =>
        {
            var d = await sp.GetRequiredService<WebhookDeliveryService>().RunAsync(ct);
            return $"sent={d.Sent};retried={d.Retried};failed={d.Failed}";
        });

    /// <summary>Catatan job memakai scope sendiri agar kegagalan job tidak merusak baris job_runs.</summary>
    private async Task RunJobAsync(string name, CancellationToken ct, Func<IServiceProvider, Task<string?>> job)
    {
        await using var recorderScope = scopes.CreateAsyncScope();
        var recorder = recorderScope.ServiceProvider.GetRequiredService<JobRunRecorder>();
        await recorder.RunAsync(name, async () =>
        {
            await using var jobScope = scopes.CreateAsyncScope();
            return await job(jobScope.ServiceProvider);
        }, ct);
    }
}
