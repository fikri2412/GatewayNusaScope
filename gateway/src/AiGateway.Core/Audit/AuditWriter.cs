using System.Text.Json;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiGateway.Core.Audit;

/// <summary>Siapa yang bertindak pada request ini; diisi middleware dari token admin.</summary>
public sealed class CurrentActor
{
    public long? TenantId { get; set; }
    public long? UserId { get; set; }
    public string? Ip { get; set; }
}

/// <summary>
/// Catat aksi konfigurasi/akun ke <c>audit_logs</c>. Jangan pernah memasukkan secret (key provider, key klien,
/// password, token) ke <c>detail</c>.
/// ponytail: ditulis setelah mutasi selesai, bukan dalam transaksi yang sama; kegagalan hanya dilog (level Error)
/// supaya aksi yang sudah berhasil tidak ikut gagal.
/// </summary>
public sealed class AuditWriter(GatewayDbContext db, CurrentActor actor, PlatformDb platform, ILogger<AuditWriter> log)
{
    public async Task WriteAsync(
        string action, string? entity = null, string? entityId = null, object? detail = null,
        long? tenantId = null, long? userId = null)
    {
        var row = new AuditLog
        {
            TenantId = tenantId ?? actor.TenantId,
            UserId = userId ?? actor.UserId,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            DetailJson = detail is null ? null : JsonSerializer.Serialize(detail),
            Ip = actor.Ip,
        };

        // Predikat RLS audit_logs menolak baris yang tenant-nya bukan tenant sesi. Endpoint anonim (login)
        // belum memasang konteks tenant padahal barisnya milik tenant user, jadi konteks dipasang untuk
        // penulisan ini lalu dipulihkan agar tidak mengubah query lain di request yang sama.
        // Baris tanpa tenant (aksi platform, login platform admin, login gagal, accept-invite) hanya lolos bila
        // ditulis principal gateway_platform, jadi ditulis lewat koneksi itu. Predikat tidak dilonggarkan untuk
        // NULL: tenant akan bisa memalsukan baris audit tingkat platform. Tanpa ConnectionStrings:GatewayPlatform
        // (dev/test, RLS belum dipasang) baris itu memakai koneksi request; dengan RLS aktif ia ditolak dan hanya dilog.
        await using var platformDb = row.TenantId is null && platform.IsSeparate ? platform.CreateContext() : null;
        var target = platformDb ?? db;
        var previous = target.CurrentTenantId;
        var switched = false;
        if (row.TenantId is { } rowTenant && rowTenant != previous)
        {
            target.CurrentTenantId = rowTenant;
            switched = true;
        }

        try
        {
            target.AuditLogs.Add(row);
            await target.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            target.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            log.LogError(ex, "Gagal menulis audit log {Action}", action);
        }
        finally
        {
            if (switched) target.CurrentTenantId = previous;
        }
    }
}
