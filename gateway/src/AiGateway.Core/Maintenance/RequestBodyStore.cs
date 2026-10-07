using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiGateway.Core.Maintenance;

/// <summary>
/// Simpan isi request/response bila <c>projects.log_content</c> aktif. Dipanggil data plane SETELAH usage log
/// tersimpan (memakai <see cref="UsageLog.Id"/>), dengan token internal berbatas waktu — bukan token request,
/// supaya tulisan tidak batal saat klien memutus koneksi. Kegagalan tidak pernah menggagalkan permintaan.
/// </summary>
public sealed class RequestBodyStore(
    GatewayDbContext db, IOptions<MaintenanceOptions> options, TimeProvider clock, ILogger<RequestBodyStore> log)
{
    public async Task RecordAsync(UsageLog entry, string? requestJson, string? responseJson)
    {
        try
        {
            if (entry.Id <= 0 || (requestJson is null && responseJson is null)) return;

            var project = await db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == entry.ProjectId && p.TenantId == entry.TenantId);
            if (project is null || !project.LogContent) return; // opt-in: default tidak menyimpan isi

            var o = options.Value;
            var days = Math.Clamp(project.ContentRetentionDays ?? o.DefaultContentRetentionDays, 1, o.MaxContentRetentionDays);

            db.Set<RequestBody>().Add(new RequestBody
            {
                UsageLogId = entry.Id,
                TenantId = entry.TenantId,
                RequestJson = Trim(requestJson, o.MaxBodyCharacters),
                ResponseJson = Trim(responseJson, o.MaxBodyCharacters),
                ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(days),
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(o.RecordTimeoutSeconds));
            await db.SaveChangesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            log.LogError(ex, "Gagal menyimpan isi request untuk usage log {UsageLogId}", entry.Id);
        }
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "...[truncated]";
}
