using Microsoft.Extensions.Logging;

namespace OperatorAi.Shared.Queue;

/// <summary>Identitas satu proses worker on-demand: <c>dotnet &lt;dll&gt; --once --run-id &lt;id&gt;</c>.</summary>
public sealed record WorkerRun(string RunId)
{
    /// <summary>
    /// Dibaca langsung dari argumen (bukan konfigurasi): provider command-line .NET memakai token sesudah
    /// <c>--once</c> sebagai nilainya, sehingga urutan argumen akan menentukan hasilnya.
    /// </summary>
    public static WorkerRun FromArgs(string[] args)
    {
        var i = Array.IndexOf(args, "--run-id");
        return new WorkerRun(i >= 0 && i + 1 < args.Length ? args[i + 1] : Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Dipanggil di akhir proses yang dihentikan host (SIGINT/SIGTERM): tiket yang belum selesai kembali ke Pending
    /// (<see cref="QueueRepository.ReleaseRunAsync"/>). Gagal tidak boleh menghalangi proses keluar; sweep dispatcher
    /// memulihkan lewat <c>Queue:StaleLockMinutes</c>.
    /// </summary>
    public async Task ReleaseAsync(QueueRepository repo, ILogger logger)
    {
        try
        {
            var released = await repo.ReleaseRunAsync(RunId, CancellationToken.None);
            if (released > 0) logger.LogWarning("Run {RunId} dihentikan; {Released} tiket dikembalikan ke Pending", RunId, released);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} dihentikan tetapi tiketnya gagal dikembalikan ke Pending", RunId);
        }
    }
}
