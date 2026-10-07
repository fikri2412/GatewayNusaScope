using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiGateway.Core.Maintenance;

/// <summary>
/// Catat satu eksekusi job ke <c>job_runs</c> (running → succeeded/failed). Kegagalan job tidak boleh
/// menghentikan pekerja; detail dibatasi 400 karakter.
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
        catch (Exception ex)
        {
            run.Status = JobRunStatuses.Failed;
            run.Detail = Trim(ex.Message);
            log.LogError(ex, "Job {JobName} gagal", jobName);
        }

        run.FinishedAt = clock.GetUtcNow().UtcDateTime;
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex) { log.LogError(ex, "Gagal menutup catatan job {JobName}", jobName); }
    }

    private static string? Trim(string? detail) =>
        string.IsNullOrEmpty(detail) || detail.Length <= 400 ? detail : detail[..400];
}
