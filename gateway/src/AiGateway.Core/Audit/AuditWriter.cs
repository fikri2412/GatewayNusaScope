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
public sealed class AuditWriter(GatewayDbContext db, CurrentActor actor, ILogger<AuditWriter> log)
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
        // ponytail: aksi tingkat platform (dan login gagal/accept-invite) menulis tenant_id NULL; baris itu
        // tetap ditolak predikat RLS saat aplikasi memakai principal gateway_app. Hanya koneksi principal
        // gateway_platform yang boleh menulisnya dan plane platform HTTP belum punya koneksi itu
        // (upgrade path di DECISIONS G4). Predikat tidak dilonggarkan untuk NULL: tenant akan bisa
        // memalsukan baris audit tingkat platform.
        var previous = db.CurrentTenantId;
        var switched = false;
        if (row.TenantId is { } rowTenant && rowTenant != previous)
        {
            db.CurrentTenantId = rowTenant;
            switched = true;
        }

        try
        {
            db.AuditLogs.Add(row);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            log.LogError(ex, "Gagal menulis audit log {Action}", action);
        }
        finally
        {
            if (switched) db.CurrentTenantId = previous;
        }
    }
}
