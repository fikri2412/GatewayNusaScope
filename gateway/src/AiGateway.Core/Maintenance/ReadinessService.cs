using AiGateway.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Maintenance;

/// <summary>Hasil readiness: <see cref="Checks"/> hanya status per pemeriksaan ("ok", "fail", "pending:N", "stale", "skipped"), tanpa detail galat.</summary>
public sealed record ReadinessReport(bool Ready, IReadOnlyDictionary<string, string> Checks);

/// <summary>
/// GB-05: kesiapan melayani lalu lintas. Berbeda dari liveness (proses hidup): database terjangkau, tidak ada migration yang
/// belum diterapkan, dan job maintenance terakhir berhasil dalam batas waktu. Tidak pernah memanggil provider upstream
/// (kegagalan provider bukan alasan gateway keluar dari load balancer; itu urusan fallback/cooldown).
/// </summary>
public sealed class ReadinessService(
    IServiceScopeFactory scopes, MaintenanceHeartbeat heartbeat, IOptions<MaintenanceOptions> options, TimeProvider clock,
    ILogger<ReadinessService> log)
{
    /// <summary>Hasil "tidak ada migration tertunda" dipakai ulang selama ini supaya probe sering tidak membaca riwayat migration tiap kali.</summary>
    private static readonly TimeSpan MigrationsCacheFor = TimeSpan.FromSeconds(60);

    private DateTimeOffset migrationsOkUntil = DateTimeOffset.MinValue;

    /// <summary>Batas basi worker: tiga siklus, minimal dua menit (siklus pertama baru selesai setelah <c>CycleSeconds</c>).</summary>
    public TimeSpan MaintenanceStaleAfter => TimeSpan.FromSeconds(Math.Max(120, 3 * options.Value.CycleSeconds));

    public async Task<ReadinessReport> CheckAsync(CancellationToken ct)
    {
        var checks = new Dictionary<string, string>();
        var database = await DatabaseAsync(checks, ct);
        if (!database) checks["migrations"] = "skipped";
        var now = clock.GetUtcNow();
        var migrationsOk = database && (now < migrationsOkUntil || await MigrationsAsync(checks, now, ct));
        if (database && migrationsOk) checks.TryAdd("migrations", "ok");

        var last = heartbeat.LastSuccess(MaintenanceHeartbeat.ReadinessJob) ?? heartbeat.StartedAt;
        var maintenanceOk = now - last <= MaintenanceStaleAfter;
        checks["maintenance"] = maintenanceOk ? "ok" : "stale";

        return new ReadinessReport(database && migrationsOk && maintenanceOk, checks);
    }

    private async Task<bool> DatabaseAsync(Dictionary<string, string> checks, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var ok = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.CanConnectAsync(ct);
            checks["database"] = ok ? "ok" : "fail";
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Readiness: database tidak terjangkau");
            checks["database"] = "fail";
            return false;
        }
    }

    private async Task<bool> MigrationsAsync(Dictionary<string, string> checks, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var pending = (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.GetPendingMigrationsAsync(ct)).Count();
            if (pending == 0)
            {
                migrationsOkUntil = now + MigrationsCacheFor;
                checks["migrations"] = "ok";
                return true;
            }
            checks["migrations"] = $"pending:{pending}";
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Readiness: migration tidak bisa diperiksa");
            checks["migrations"] = "fail";
            return false;
        }
    }
}
