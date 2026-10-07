using System.Data;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiGateway.Core.Proxy;

public sealed class UsageRecorder(GatewayDbContext db, ILogger<UsageRecorder> log)
{
    // Parameter bernama dengan presisi eksplisit: parameter decimal bawaan memotong ke 2 desimal (biaya kecil jadi 0).
    private const string UpsertDaily = """
        MERGE usage_daily WITH (HOLDLOCK) AS t
        USING (SELECT @tenant AS tenant_id, @project AS project_id, @key AS api_key_id, @model AS model_id, @day AS day) AS s
          ON t.tenant_id = s.tenant_id AND t.project_id = s.project_id AND t.api_key_id = s.api_key_id
             AND t.model_id = s.model_id AND t.day = s.day
        WHEN MATCHED THEN UPDATE SET requests = t.requests + 1, denied = t.denied + @denied, errors = t.errors + @errors,
             input_tokens = t.input_tokens + @in, output_tokens = t.output_tokens + @out, cost = t.cost + @cost
        WHEN NOT MATCHED THEN INSERT (tenant_id, project_id, api_key_id, model_id, day, requests, denied, errors, input_tokens, output_tokens, cost)
             VALUES (@tenant, @project, @key, @model, @day, 1, @denied, @errors, @in, @out, @cost);
        """;

    /// <summary>Catat penolakan untuk key yang sudah terverifikasi.</summary>
    public Task RecordDeniedAsync(GatewayCaller caller, int httpStatus, string reason) =>
        RecordAsync(new UsageLog
        {
            TenantId = caller.TenantId,
            ProjectId = caller.ProjectId,
            ApiKeyId = caller.Key.Id,
            RequestId = caller.RequestId,
            Status = UsageStatuses.Denied,
            HttpStatus = httpStatus,
            DeniedReason = reason,
            ClientIp = caller.ClientIp,
            UserAgent = caller.UserAgent,
            Tags = caller.Tags,
        });

    /// <summary>
    /// Simpan baris log dan naikkan agregat harian dalam satu transaksi. Tidak memakai token pembatalan
    /// permintaan: biaya upstream sudah terjadi walau klien memutus koneksi. Kegagalan hanya dilog.
    /// </summary>
    public async Task RecordAsync(UsageLog entry)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.UsageLogs.Add(entry);
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(UpsertDaily,
                new SqlParameter("@tenant", entry.TenantId),
                new SqlParameter("@project", entry.ProjectId),
                new SqlParameter("@key", entry.ApiKeyId),
                new SqlParameter("@model", entry.ModelId ?? 0),
                new SqlParameter("@day", SqlDbType.Date) { Value = entry.CreatedAt.Date },
                new SqlParameter("@denied", entry.Status == UsageStatuses.Denied ? 1 : 0),
                new SqlParameter("@errors", entry.Status == UsageStatuses.Error ? 1 : 0),
                new SqlParameter("@in", (long)entry.InputTokens),
                new SqlParameter("@out", (long)entry.OutputTokens),
                new SqlParameter("@cost", SqlDbType.Decimal) { Precision = 18, Scale = 8, Value = entry.Cost });
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            log.LogError(ex, "Gagal mencatat pemakaian request {RequestId} tenant {TenantId}", entry.RequestId, entry.TenantId);
        }
    }
}
