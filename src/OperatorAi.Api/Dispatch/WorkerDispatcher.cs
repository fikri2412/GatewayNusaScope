using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OperatorAi.Api.Hubs;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Api.Dispatch;

/// <summary>
/// Satu-satunya pihak yang menjalankan worker (DESIGN bagian 6.1): <c>dotnet &lt;dll&gt; --once --run-id &lt;guid&gt;</c>,
/// satu proses per tiket. Bangun oleh <see cref="Signal"/>, oleh keluarnya proses anak (kode 0), atau tiap
/// <c>Dispatcher:SweepSeconds</c>. Proses anak yang crash dipulihkan lewat run id-nya, tanpa sinyal ulang
/// (sweep yang menjalankan ulang) supaya konfigurasi rusak tidak jadi loop rapat.
/// </summary>
public sealed class WorkerDispatcher(
    QueueRepository repo,
    IOptionsMonitor<DispatcherOptions> options,
    IHostEnvironment env,
    ChatNotifier notifier,
    ILogger<WorkerDispatcher> logger) : BackgroundService
{
    private enum Kind { Operator, Specialist }

    // Satu sinyal tertunda cukup: putaran menghitung ulang semuanya dari tabel.
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly ConcurrentDictionary<string, Kind> _alive = new();

    public void Signal() => _signal.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var subscription = options.OnChange(_ => Signal());
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Putaran dispatcher gagal; dicoba lagi pada sweep berikutnya");
            }
            await WaitForWakeAsync(TimeSpan.FromSeconds(options.CurrentValue.SweepSeconds), ct);
        }
    }

    private async Task WaitForWakeAsync(TimeSpan sweep, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(sweep);
        try
        {
            await _signal.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Sweep habis (atau shutdown; loop memeriksa ct).
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;

        var recovered = await repo.RecoverStaleAsync(ct);
        if (recovered.Recovered > 0) logger.LogWarning("Memulihkan {Count} tiket macet", recovered.Recovered);
        await PublishRecoveryAsync(recovered, ct);

        var operatorRuns = RunIds(Kind.Operator);
        var specialistRuns = RunIds(Kind.Specialist);
        var operators = await SpawnCountAsync(await repo.CountEligibleQueueItemsAsync(ct), operatorRuns,
            o.MaxConcurrentOperators, repo.CountHeldQueueItemsAsync, ct);
        var specialists = await SpawnCountAsync(await repo.CountEligibleJobsAsync(JobTypes.All, ct), specialistRuns,
            o.MaxConcurrentSpecialists, repo.CountHeldJobsAsync, ct);

        for (var i = 0; i < operators; i++) Start(Kind.Operator, o.OperatorDll);
        for (var i = 0; i < specialists; i++) Start(Kind.Specialist, o.SpecialistDll);
    }

    /// <summary>Permintaan maaf dan perubahan status job akibat recovery belum sampai ke browser; dorong sekarang.</summary>
    private async Task PublishRecoveryAsync(RecoveryResult recovered, CancellationToken ct)
    {
        foreach (var a in recovered.Apologies)
            await notifier.PublishMessageAsync(a.ConversationId, a.MessageId, ct);
        foreach (var j in recovered.JobUpdates)
            await notifier.PublishJobAsync(j.ConversationId, j.JobId, ct);
        foreach (var conversationId in recovered.QueueUpdates.Select(q => q.ConversationId).Distinct())
            await notifier.PublishActivityAsync(conversationId, ct);
    }

    private List<string> RunIds(Kind kind) => _alive.Where(p => p.Value == kind).Select(p => p.Key).ToList();

    private int Alive(Kind kind) => _alive.Values.Count(k => k == kind);

    /// <summary>
    /// min(memenuhi syarat − hidup-tapi-belum-mengklaim, batas − hidup). Pengurangan pertama mencegah proses
    /// berlebih saat tiket yang sama sedang ditunggu proses yang masih cold start (beberapa detik).
    /// </summary>
    private static async Task<int> SpawnCountAsync(int eligible, List<string> aliveRuns, int limit,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<int>> countHeld, CancellationToken ct)
    {
        var unclaimed = aliveRuns.Count - await countHeld(aliveRuns, ct);
        return Math.Min(eligible - unclaimed, limit - aliveRuns.Count);
    }

    private void Start(Kind kind, string dll)
    {
        var path = Path.GetFullPath(dll, env.ContentRootPath);
        if (!File.Exists(path))
        {
            logger.LogError("DLL worker {Kind} tidak ditemukan: {Path} (build project worker dulu)", kind, path);
            return;
        }

        var runId = Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(path)!, // appsettings worker ada di folder output
            UseShellExecute = false,
            CreateNoWindow = true,
            // Worker tanpa jendela: tanpa ini lognya (timing, notify gagal, stream gagal) tidak terlihat di mana pun.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("--once");
        psi.ArgumentList.Add("--run-id");
        psi.ArgumentList.Add(runId);
        psi.Environment["DOTNET_ENVIRONMENT"] = env.EnvironmentName; // host worker membaca DOTNET_, bukan ASPNETCORE_

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line) logger.LogInformation("[{Kind} {RunId}] {Line}", kind, runId[..8], line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line) logger.LogWarning("[{Kind} {RunId}] {Line}", kind, runId[..8], line);
        };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            process.Dispose();
            logger.LogError(ex, "Gagal menjalankan worker {Kind}", kind);
            return;
        }

        _alive[runId] = kind;
        logger.LogInformation("Worker {Kind} run {RunId} dijalankan (hidup: {Alive})", kind, runId, Alive(kind));
        _ = WatchAsync(process, runId, kind);
    }

    private async Task WatchAsync(Process process, string runId, Kind kind)
    {
        int code;
        using (process)
        {
            await process.WaitForExitAsync();
            code = process.ExitCode;
        }
        _alive.TryRemove(runId, out _);

        try
        {
            if (code == 0)
            {
                Signal(); // tiket berikutnya (mis. satu percakapan) mungkin kini memenuhi syarat
                return;
            }
            var recovered = await repo.RecoverRunAsync(runId);
            logger.LogWarning("Worker {Kind} run {RunId} keluar dengan kode {Code}; {Recovered} tiket dipulihkan",
                kind, runId, code, recovered.Recovered);
            await PublishRecoveryAsync(recovered, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pemulihan setelah worker {Kind} run {RunId} gagal; sweep akan memulihkan", kind, runId);
        }
    }
}
