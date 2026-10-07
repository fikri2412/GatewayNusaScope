using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiGateway.Core.Maintenance;

/// <summary>
/// Catat satu eksekusi job ke <c>job_runs</c> (running → succeeded/failed). Kegagalan job tidak boleh
/// menghentikan pekerja; detail dibatasi 400 karakter. Pembatalan saat host berhenti menutup baris tanpa
/// menandainya gagal, dan baris penutup disimpan dengan token sendiri supaya tidak tertinggal "running".
/// </summary>
public sealed class JobRunRecorder(GatewayDbContext db, TimeProvider clock, ILogger<JobRunRecorder> log)
{
    public async Task RunAsync(string jobName, Func<Task<string?>> job, CancellationToken ct)
    {
        var run = new JobRun { JobName = jobName, StartedAt = clock.GetUtcNow().UtcDateTime };
        try
        {
            db.Set<JobRun>().Add(run);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Gagal mencatat awal job {JobName}", jobName);
            return;
        }

        try
        {
            run.Detail = Trim(await job());
            run.Status = JobRunStatuses.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Penghentian host bukan kegagalan job: baris ditutup tanpa status failed (skema hanya punya
            // running/succeeded/failed).
            run.Detail = "dibatalkan: penghentian host";
        }
        catch (Exception ex)
        {
            run.Status = JobRunStatuses.Failed;
            run.Detail = Trim(ex.Message);
            log.LogError(ex, "Job {JobName} gagal", jobName);
        }

        run.FinishedAt = clock.GetUtcNow().UtcDateTime;
        // Token sendiri: saat host berhenti, ct sudah batal dan baris akan tertinggal "running" tanpa penutup.
        try { await db.SaveChangesAsync(CancellationToken.None); }
        catch (Exception ex) { log.LogError(ex, "Gagal menutup catatan job {JobName}", jobName); }
    }

    private static string? Trim(string? detail) =>
        string.IsNullOrEmpty(detail) || detail.Length <= 400 ? detail : detail[..400];
}
