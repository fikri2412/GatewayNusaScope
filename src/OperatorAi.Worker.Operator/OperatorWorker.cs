using System.Diagnostics;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Worker.Operator;

/// <summary>
/// Satu proses = satu queue item (DESIGN bagian 9): klaim, proses lewat <see cref="TicketProcessor"/>, keluar. Tidak polling.
/// </summary>
public sealed class OperatorWorker(
    WorkerRun run,
    QueueRepository repo,
    NotifyClient notify,
    TicketProcessor processor,
    IHostApplicationLifetime lifetime,
    ILogger<OperatorWorker> logger) : BackgroundService
{
    private QueueItem? _claimed;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Dihentikan host (SIGINT/SIGTERM); tiket dikembalikan di finally. Bukan kegagalan.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bukan kegagalan tiket (itu ditangani di RunOnceAsync): koneksi database putus dsb. Kode bukan 0
            // membuat dispatcher memulihkan tiket milik run id ini.
            logger.LogError(ex, "Run {RunId} berhenti dengan error", run.RunId);
            Environment.ExitCode = 1;
        }
        finally
        {
            // Dihentikan host di tengah tiket: kembalikan ke Pending sekarang, bukan menunggu StaleLockMinutes.
            if (ct.IsCancellationRequested) await run.ReleaseAsync(repo, logger);
            if (ct.IsCancellationRequested && _claimed is { } item)
                await notify.NotifyAsync(NotifyTypes.ActivityChanged, item.ConversationId, item.Id);
            lifetime.StopApplication();
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var bootMs = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; // runtime + host + DI sampai worker jalan
        var recovered = await repo.RecoverStaleAsync(ct);
        await notify.NotifyMessagesAsync(recovered.Apologies, ct);
        await notify.NotifyJobsAsync(recovered.JobUpdates, ct);
        foreach (var q in recovered.QueueUpdates)
            await notify.NotifyAsync(NotifyTypes.ActivityChanged, q.ConversationId, q.QueueItemId, ct);

        var claimStarted = Stopwatch.StartNew();
        var item = await repo.ClaimQueueItemAsync(run.RunId, ct);
        var claimMs = claimStarted.ElapsedMilliseconds;
        if (item is null)
        {
            logger.LogInformation("Run {RunId}: tidak ada tiket yang memenuhi syarat", run.RunId);
            return;
        }
        _claimed = item;

        using var _ = logger.BeginScope("conversationId={ConversationId} queueItemId={QueueItemId}",
            item.ConversationId, item.Id);
        // Memecah jeda per pesan: antre sejak enqueue (dispatcher + proses baru) dan boot proses sampai klaim berhasil.
        logger.LogInformation(
            "Run {RunId} mengklaim {Type}; timing: sejak enqueue={SinceEnqueueMs}ms, boot proses={BootMs}ms, sejak proses start={SinceStartMs}ms, klaim={ClaimMs}ms",
            run.RunId, item.Type, (long)(DateTime.UtcNow - DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc)).TotalMilliseconds,
            bootMs, (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds, claimMs);
        var handling = Stopwatch.StartNew();

        try
        {
            await processor.HandleAsync(item, ct);
            logger.LogInformation("Selesai; timing: pemrosesan tiket={ProcessMs}ms, total sejak proses start={TotalMs}ms",
                handling.ElapsedMilliseconds, (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is LostTicketException or LeaseLostException)
        {
            // Tiket bukan lagi milik run ini (sudah dipulihkan/diambil proses lain): jangan menulis apa pun.
            logger.LogWarning("{Message}; hasil run ini tidak dicatat", ex.Message);
        }
        catch (AiModelUnavailableException ex)
        {
            // Model dicabut gateway: percobaan dengan model yang sama pasti gagal lagi, jadi tiket dihentikan
            // sekarang dan user diberi balasan jelas.
            logger.LogWarning("Tiket {QueueItemId} dihentikan: {Message}", item.Id, ex.Message);
            var failed = await repo.FailQueueItemTerminallyAsync(item.Id, run.RunId, ex.Message, Replies.ModelUnavailable, ct);
            if (failed?.Apology is { } reply) await notify.NotifyMessagesAsync([reply], ct);
        }
        catch (AiFirstByteTimeoutException ex)
        {
            // Penyedia tidak mengirim byte pertama tepat waktu: mengulang langsung hanya memperpanjang penantian user
            // (antrean penyedia mulai dari nol), jadi tiket dihentikan sekarang dengan balasan yang jelas.
            logger.LogWarning("Tiket {QueueItemId} dihentikan: {Message}", item.Id, ex.Message);
            var failed = await repo.FailQueueItemTerminallyAsync(item.Id, run.RunId, ex.Message, Replies.ProviderSlow, ct);
            if (failed?.Apology is { } reply) await notify.NotifyMessagesAsync([reply], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Tiket gagal");
            var failed = await repo.FailQueueItemAsync(item.Id, run.RunId, ex.Message, ct);
            if (failed?.Apology is { } apology) await notify.NotifyMessagesAsync([apology], ct);
            else if (failed is not null)
                await notify.NotifyAsync(NotifyTypes.ActivityChanged, item.ConversationId, item.Id, ct);
        }
    }
}
