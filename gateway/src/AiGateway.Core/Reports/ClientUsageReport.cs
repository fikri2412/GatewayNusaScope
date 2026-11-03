using System.Globalization;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Proxy;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Reports;

/// <summary>Pemakaian satu key pada satu hari UTC. Token/biaya adalah angka yang diketahui; lihat <see cref="IncompleteUsageRequests"/>.</summary>
public sealed record ClientUsageDay(
    string Day, long Requests, long Denied, long Errors, long InputTokens, long OutputTokens, decimal Cost, long IncompleteUsageRequests);

/// <param name="Days">Setiap hari pada rentang (juga yang tanpa pemakaian, bernilai 0), urut naik.</param>
/// <param name="Total">Jumlah <paramref name="Days"/>.</param>
/// <param name="Quota">Sisa kuota efektif saat ini (bukan per rentang).</param>
public sealed record ClientUsage(
    string From, string To, IReadOnlyList<ClientUsageDay> Days, ClientUsageDay Total, ClientQuota Quota);

/// <summary>
/// GB-04: pemakaian dan sisa kuota untuk pemanggil API key, read-only. Hanya data key itu sendiri (bukan project atau tenant),
/// dari agregat harian yang sama dengan laporan admin (<c>usage_daily</c>), jadi angkanya sama. Tidak menulis <c>usage_logs</c>.
/// </summary>
public sealed class ClientUsageReport(GatewayDbContext db, PolicyEngine policy, TimeProvider clock)
{
    /// <summary>Rentang maksimum (hari, inklusif).</summary>
    public const int MaxRangeDays = 31;

    /// <summary>Bawaan: tujuh hari terakhir termasuk hari ini (UTC).</summary>
    public const int DefaultRangeDays = 7;

    /// <summary>Pemanggil sudah lolos autentikasi; tenant konteks sudah diset dari key (tidak pernah dari klien).</summary>
    public async Task<ClientUsage> RunAsync(GatewayCaller caller, string? from, string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to is null ? today : ParseDay(to, "to");
        var start = from is null ? end.AddDays(1 - DefaultRangeDays) : ParseDay(from, "from");
        if (end < start) throw GatewayException.BadRequest("invalid_range", "'to' tidak boleh sebelum 'from'.");
        if (end.DayNumber - start.DayNumber + 1 > MaxRangeDays)
            throw GatewayException.BadRequest("range_too_large", $"Rentang maksimal {MaxRangeDays} hari.");

        var keyId = caller.Key.Id;
        var rows = await db.UsageDailies.AsNoTracking()
            .Where(u => u.ApiKeyId == keyId && u.Day >= start && u.Day <= end)
            .GroupBy(u => u.Day)
            .Select(g => new
            {
                Day = g.Key, Requests = g.Sum(u => u.Requests), Denied = g.Sum(u => u.Denied), Errors = g.Sum(u => u.Errors),
                In = g.Sum(u => u.InputTokens), Out = g.Sum(u => u.OutputTokens), Cost = g.Sum(u => u.Cost),
                Incomplete = g.Sum(u => u.IncompleteUsageRequests),
            }).ToListAsync(ct);
        var byDay = rows.ToDictionary(r => r.Day);

        var days = new List<ClientUsageDay>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var label = Label(day);
            days.Add(byDay.TryGetValue(day, out var r)
                ? new ClientUsageDay(label, r.Requests, r.Denied, r.Errors, r.In, r.Out, r.Cost, r.Incomplete)
                : new ClientUsageDay(label, 0, 0, 0, 0, 0, 0m, 0));
        }
        var total = new ClientUsageDay("total", days.Sum(d => d.Requests), days.Sum(d => d.Denied), days.Sum(d => d.Errors),
            days.Sum(d => d.InputTokens), days.Sum(d => d.OutputTokens), days.Sum(d => d.Cost), days.Sum(d => d.IncompleteUsageRequests));

        return new ClientUsage(Label(start), Label(end), days, total, await policy.QuotaAsync(caller, ct));
    }

    private static string Label(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string value, string name) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw GatewayException.BadRequest("invalid_range", $"'{name}' harus tanggal YYYY-MM-DD.");
}
