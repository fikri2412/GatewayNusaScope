using System.Collections.Concurrent;

namespace AiGateway.Core.Maintenance;

/// <summary>
/// GB-05: kapan setiap job maintenance terakhir <b>berhasil</b> di proses ini (di memori, dipakai readiness dan metrik).
/// Job yang gagal tidak memperbarui; <see cref="StartedAt"/> memberi masa tenggang sebelum siklus pertama selesai.
/// </summary>
public sealed class MaintenanceHeartbeat(TimeProvider clock)
{
    /// <summary>Nama job yang dipakai readiness: berjalan tiap siklus (bukan hanya per jam/5 menit).</summary>
    public const string ReadinessJob = "maintenance.webhook_delivery";

    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSuccess = new();

    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();

    public void RecordSuccess(string job) => lastSuccess[job] = clock.GetUtcNow();

    public DateTimeOffset? LastSuccess(string job) => lastSuccess.TryGetValue(job, out var at) ? at : null;

    /// <summary>Salinan untuk metrik; jumlah job tetap kecil (nama konstan di <see cref="MaintenanceWorker"/>).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Snapshot() => new Dictionary<string, DateTimeOffset>(lastSuccess);
}
