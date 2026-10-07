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
    public string? Role { get; set; }
    public string? Ip { get; set; }
}

/// <summary>
/// Catat aksi konfigurasi/akun ke <c>audit_logs</c>. Jangan pernah memasukkan secret (key provider, key klien,
/// password, token) ke <c>detail</c>.
/// ponytail: ditulis setelah mutasi selesai, bukan dalam transaksi yang sama; kegagalan hanya dilog.
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
    }
}
