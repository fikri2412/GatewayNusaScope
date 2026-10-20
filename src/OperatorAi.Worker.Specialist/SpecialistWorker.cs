using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Worker.Specialist;

/// <summary>
/// Satu proses = satu job (DESIGN bagian 11): klaim job bertipe yang punya handler (dependensi sudah Done), jalankan,
/// catat hasil, notify, keluar. Gagal → aturan retry bagian 6 (gagal permanen: dependen ikut gagal, operator diberi tahu).
/// Snapshot kebijakan dan pesan sumber dibaca dari baris job; kepemilikan dijaga lease berdetak selama job berjalan.
/// </summary>
public sealed class SpecialistWorker(
    WorkerRun run,
    QueueRepository repo,
    NotifyClient notify,
    IEnumerable<IJobHandler> handlers,
    DbContextOptions<AppDbContext> dbOptions,
    IHostApplicationLifetime lifetime,
    ILogger<SpecialistWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Dihentikan host (SIGINT/SIGTERM); job dikembalikan di finally. Bukan kegagalan.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Kode bukan 0 membuat dispatcher memulihkan job milik run id ini.
            logger.LogError(ex, "Run {RunId} berhenti dengan error", run.RunId);
            Environment.ExitCode = 1;
        }
        finally
        {
            // Dihentikan host di tengah job: kembalikan ke Pending sekarang, bukan menunggu StaleLockMinutes.
            if (ct.IsCancellationRequested) await run.ReleaseAsync(repo, logger);
            lifetime.StopApplication();
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // Recovery menyentuh queue_items dan jobs: permintaan maaf dan perubahan status job harus sampai ke browser.
        var recovered = await repo.RecoverStaleAsync(ct);
        await notify.NotifyMessagesAsync(recovered.Apologies, ct);
        await notify.NotifyJobsAsync(recovered.JobUpdates, ct);
        foreach (var q in recovered.QueueUpdates)
            await notify.NotifyAsync(NotifyTypes.ActivityChanged, q.ConversationId, q.QueueItemId, ct);

        var byType = handlers.ToDictionary(h => h.JobType);
        var job = await repo.ClaimJobAsync(run.RunId, byType.Keys.ToList(), ct);
        if (job is null)
        {
            logger.LogInformation("Run {RunId}: tidak ada job yang memenuhi syarat", run.RunId);
            return;
        }

        using var _ = logger.BeginScope("conversationId={ConversationId} jobId={JobId}", job.ConversationId, job.Id);
        logger.LogInformation("Run {RunId} mengklaim {JobType}", run.RunId, job.JobType);

        JobContext? context = null;
        try
        {
            // Lease berdetak: hilang → token dibatalkan dan tidak ada penulisan hasil yang tidak lagi kita miliki.
            await using var lease = await repo.KeepAliveAsync(job.Id, run.RunId, isJob: true, ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
            var token = linked.Token;

            var policy = ExecutionPolicy.FromJson(job.ExecutionPolicyJson);
            // Model workflow dibaca sekali dari input job, sebelum job dijalankan: percakapan bisa sudah memakai
            // model lain sekarang, dan job ini tidak pernah ikut berubah karenanya.
            var model = ModelSnapshot.Read(job.InputJson);
            var sourceMessageId = job.SourceMessageId
                ?? throw new InvalidOperationException($"Job {job.Id} tanpa pesan sumber.");
            await using (var db = new AppDbContext(dbOptions))
            {
                var source = await db.Messages.AsNoTracking()
                    .Where(m => m.Id == sourceMessageId && m.ConversationId == job.ConversationId && m.Role == MessageRoles.User)
                    .Select(m => new { m.ModelAlias }).SingleOrDefaultAsync(token);
                if (source is null)
                    throw new InvalidOperationException($"Pesan sumber {sourceMessageId} bukan pesan user di percakapan {job.ConversationId}.");
                // Pesan lama (sebelum 6A2) belum punya model; job-nya tetap memakai model snapshot yang sama.
                // Job alternatif (regenerate/bandingkan) sengaja memakai model pilihan branch, jadi tidak dicek.
                if (job.IsPrimary && source.ModelAlias is { } sourceAlias && sourceAlias != model)
                    throw new InvalidOperationException(
                        $"Model pesan sumber '{sourceAlias}' tidak sama dengan model job '{model}' di percakapan {job.ConversationId}.");
            }

            string? dependencyOutput = null;
            if (job.DependsOnJobId is { } dependencyId)
            {
                await using var db = new AppDbContext(dbOptions);
                var expectedType = job.JobType switch
                {
                    JobTypes.Pdf => JobTypes.Analyze,
                    JobTypes.DocumentAnalyze or JobTypes.DiagramPlan => JobTypes.DocumentRead,
                    JobTypes.MermaidRender => JobTypes.DiagramPlan,
                    _ => JobTypes.ExcelRead,
                };
                dependencyOutput = await db.Jobs.AsNoTracking()
                    .Where(d => d.Id == dependencyId && d.ConversationId == job.ConversationId
                        && d.Status == Statuses.Done && d.JobType == expectedType)
                    .Select(d => d.OutputJson).SingleOrDefaultAsync(token)
                    ?? throw new InvalidOperationException("Output job dependensi tidak tersedia di percakapan ini.");
            }

            context = new JobContext(job, dependencyOutput, policy, sourceMessageId, model);
            var output = await byType[job.JobType].HandleAsync(context, token);
            if (await repo.CompleteJobAsync(job.Id, run.RunId, output, token))
            {
                logger.LogInformation("Selesai");
                await notify.NotifyJobsAsync([new JobRef(job.ConversationId, job.Id)], ct);
            }
            else
            {
                logger.LogWarning("Job sudah dipulihkan/diambil proses lain; hasil run ini tidak dicatat");
            }
        }
        catch (LeaseLostException ex)
        {
            logger.LogWarning("{Message}; hasil run ini tidak dicatat", ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Pembatalan bukan dari host: lease hilang di tengah job, job itu bukan lagi milik run ini.
            logger.LogWarning("Job {JobId} bukan lagi milik run {RunId}; hasil run ini tidak dicatat", job.Id, run.RunId);
        }
        catch (AiModelUnavailableException ex)
        {
            // Model dicabut gateway: percobaan ulang dengan model yang sama pasti gagal lagi, jadi job (dan semua
            // dependensinya) langsung gagal permanen dan callback ke operator membawa kategori penyebabnya.
            logger.LogWarning("Job {JobId} dihentikan: {Message}", job.Id, ex.Message);
            var failed = await repo.FailJobTerminallyAsync(job.Id, run.RunId, ex.Message, ct);
            if (failed is not null) await notify.NotifyJobsAsync(failed.Updated, ct);
        }
        catch (DocumentInputException ex)
        {
            // Dokumen rusak/terkunci/melewati batas/tanpa dukungan vision: pengulangan tidak akan mengubah hasil.
            // Kategori dokumen dipisahkan dari modelUnavailable supaya operator tidak menyarankan ganti model.
            logger.LogWarning("Job {JobId} dihentikan: {Message}", job.Id, ex.Message);
            var failed = await repo.FailJobTerminallyAsync(job.Id, run.RunId, ex.Message, ct, FailureCategories.DocumentInvalid);
            if (failed is not null) await notify.NotifyJobsAsync(failed.Updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Job gagal");
            var failed = await repo.FailJobAsync(job.Id, run.RunId, ex.Message, ct);
            if (failed is not null) await notify.NotifyJobsAsync(failed.Updated, ct);
        }
        finally
        {
            // Token sudah terpakai walau job gagal atau proses dihentikan, jadi dicatat tanpa token pembatalan.
            if (context is { } meter && (meter.Tokens.Total > 0 || !meter.Tokens.UsageAvailable))
                await AddTokensAsync(job.ConversationId, meter.Tokens.Total, meter.Tokens.UsageAvailable);
        }
    }

    private async Task AddTokensAsync(Guid conversationId, long tokens, bool usageAvailable)
    {
        try
        {
            await using var db = new AppDbContext(dbOptions);
            await db.AddTokensAsync(conversationId, tokens, usageAvailable, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gagal mencatat {Tokens} token ke percakapan {ConversationId}", tokens, conversationId);
        }
    }
}
