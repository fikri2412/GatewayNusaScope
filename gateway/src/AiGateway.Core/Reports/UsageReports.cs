using System.Globalization;
using System.Text;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Reports;

public sealed record UsageRow(
    string Key, string Label, long Requests, long Denied, long Errors, long InputTokens, long OutputTokens, decimal Cost);

public sealed record UsageFilter(
    DateTime From, DateTime To, long? ProjectId = null, long? ApiKeyId = null, long? ModelId = null, string? Status = null);

public sealed record UsageLogRow(
    long Id, DateTime CreatedAt, Guid RequestId, long ProjectId, long ApiKeyId, long? ModelId, string Status, int HttpStatus,
    string? DeniedReason, int InputTokens, int OutputTokens, int CachedTokens, int ReasoningTokens, decimal Cost, int LatencyMs,
    int Attempts, bool FallbackUsed, string? EndUser, string? Tags);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize);

/// <summary>Laporan pemakaian tenant konteks (memakai query filter) atau seluruh platform (hanya platform admin).</summary>
public sealed class UsageReports(GatewayDbContext db)
{
    public const int MaxPageSize = 200;
    public const int MaxExportRows = 100_000;
    public const int MaxRangeDays = 366;

    public static readonly string[] GroupBys = ["day", "project", "model", "key"];

    public async Task<List<UsageRow>> SummaryAsync(DateOnly from, DateOnly to, string groupBy, CancellationToken ct)
    {
        ValidateRange(from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MinValue));
        var q = db.UsageDailies.AsNoTracking().Where(u => u.Day >= from && u.Day <= to);
        switch (groupBy)
        {
            case "day":
                var days = await q.GroupBy(u => u.Day).Select(g => new
                {
                    g.Key, Requests = g.Sum(u => u.Requests), Denied = g.Sum(u => u.Denied), Errors = g.Sum(u => u.Errors),
                    In = g.Sum(u => u.InputTokens), Out = g.Sum(u => u.OutputTokens), Cost = g.Sum(u => u.Cost),
                }).ToListAsync(ct);
                return days.OrderBy(d => d.Key).Select(d =>
                {
                    var label = d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return new UsageRow(label, label, d.Requests, d.Denied, d.Errors, d.In, d.Out, d.Cost);
                }).ToList();
            case "project":
                return Label(await GroupByLongAsync(q, u => u.ProjectId, ct),
                    await db.Projects.IgnoreQueryFilters().Where(p => q.Select(u => u.ProjectId).Contains(p.Id))
                        .ToDictionaryAsync(p => p.Id.ToString(), p => p.Name, ct));
            case "model":
                return Label(await GroupByLongAsync(q, u => u.ModelId, ct),
                    await db.Models.IgnoreQueryFilters().Where(m => q.Select(u => u.ModelId).Contains(m.Id))
                        .ToDictionaryAsync(m => m.Id.ToString(), m => m.Alias, ct));
            case "key":
                return Label(await GroupByLongAsync(q, u => u.ApiKeyId, ct),
                    await db.ApiKeys.Where(k => q.Select(u => u.ApiKeyId).Contains(k.Id))
                        .ToDictionaryAsync(k => k.Id.ToString(), k => k.Name, ct));
            default:
                throw GatewayException.BadRequest("invalid_group_by", "groupBy harus day, project, model, atau key.");
        }
    }

    private static async Task<List<UsageRow>> GroupByLongAsync(
        IQueryable<Domain.UsageDaily> q, System.Linq.Expressions.Expression<Func<Domain.UsageDaily, long>> key, CancellationToken ct)
    {
        var rows = await q.GroupBy(key).Select(g => new
        {
            g.Key, Requests = g.Sum(u => u.Requests), Denied = g.Sum(u => u.Denied), Errors = g.Sum(u => u.Errors),
            In = g.Sum(u => u.InputTokens), Out = g.Sum(u => u.OutputTokens), Cost = g.Sum(u => u.Cost),
        }).ToListAsync(ct);
        return rows.Select(r => new UsageRow(r.Key.ToString(CultureInfo.InvariantCulture), r.Key.ToString(CultureInfo.InvariantCulture),
            r.Requests, r.Denied, r.Errors, r.In, r.Out, r.Cost)).ToList();
    }

    private static List<UsageRow> Label(List<UsageRow> rows, Dictionary<string, string> names) =>
        rows.Select(r => r with { Label = names.TryGetValue(r.Key, out var n) ? n : r.Key == "0" ? "(tidak ada model)" : r.Key })
            .OrderByDescending(r => r.Cost).ThenByDescending(r => r.Requests).ToList();

    public async Task<Page<UsageLogRow>> LogsAsync(UsageFilter filter, int page, int pageSize, CancellationToken ct)
    {
        ValidateRange(filter.From, filter.To);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);
        var q = Filtered(filter);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(l => l.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(Row).ToListAsync(ct);
        return new Page<UsageLogRow>(items, total, page, pageSize);
    }

    /// <summary>CSV (UTF-8). Sel yang diawali = + - @ diberi tanda kutip agar tidak dieksekusi sebagai rumus spreadsheet.</summary>
    public async Task WriteCsvAsync(UsageFilter filter, Stream output, CancellationToken ct)
    {
        ValidateRange(filter.From, filter.To);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync("id,created_at,request_id,project_id,api_key_id,model_id,status,http_status,denied_reason,input_tokens,output_tokens,cached_tokens,reasoning_tokens,cost,latency_ms,attempts,fallback_used,end_user,tags");
        await foreach (var l in Filtered(filter).OrderBy(l => l.Id).Take(MaxExportRows).Select(Row).AsAsyncEnumerable().WithCancellation(ct))
        {
            await writer.WriteLineAsync(string.Join(',',
                l.Id, l.CreatedAt.ToString("O", CultureInfo.InvariantCulture), l.RequestId, l.ProjectId, l.ApiKeyId, l.ModelId,
                Csv(l.Status), l.HttpStatus, Csv(l.DeniedReason), l.InputTokens, l.OutputTokens, l.CachedTokens, l.ReasoningTokens,
                l.Cost.ToString(CultureInfo.InvariantCulture), l.LatencyMs, l.Attempts, l.FallbackUsed, Csv(l.EndUser), Csv(l.Tags)));
        }
    }

    private IQueryable<Domain.UsageLog> Filtered(UsageFilter f)
    {
        var q = db.UsageLogs.AsNoTracking().Where(l => l.CreatedAt >= f.From && l.CreatedAt < f.To);
        if (f.ProjectId is { } p) q = q.Where(l => l.ProjectId == p);
        if (f.ApiKeyId is { } k) q = q.Where(l => l.ApiKeyId == k);
        if (f.ModelId is { } m) q = q.Where(l => l.ModelId == m);
        if (f.Status is { } s) q = q.Where(l => l.Status == s);
        return q;
    }

    private static readonly System.Linq.Expressions.Expression<Func<Domain.UsageLog, UsageLogRow>> Row = l => new UsageLogRow(
        l.Id, l.CreatedAt, l.RequestId, l.ProjectId, l.ApiKeyId, l.ModelId, l.Status, l.HttpStatus, l.DeniedReason,
        l.InputTokens, l.OutputTokens, l.CachedTokens, l.ReasoningTokens, l.Cost, l.LatencyMs, l.Attempts, l.FallbackUsed, l.EndUser, l.Tags);

    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static void ValidateRange(DateTime from, DateTime to)
    {
        if (to < from) throw GatewayException.BadRequest("invalid_range", "'to' tidak boleh sebelum 'from'.");
        if ((to - from).TotalDays > MaxRangeDays) throw GatewayException.BadRequest("range_too_large", $"Rentang maksimal {MaxRangeDays} hari.");
    }

    /// <summary>Ringkasan seluruh tenant (platform admin); tanpa isi prompt.</summary>
    public async Task<List<UsageRow>> PlatformOverviewAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MinValue));
        var q = db.UsageDailies.IgnoreQueryFilters().AsNoTracking().Where(u => u.Day >= from && u.Day <= to);
        var rows = await GroupByLongAsync(q, u => u.TenantId, ct);
        var names = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id.ToString(), t => t.Name, ct);
        return rows.Select(r => r with { Label = names.GetValueOrDefault(r.Key, r.Key) }).OrderByDescending(r => r.Cost).ToList();
    }
}
