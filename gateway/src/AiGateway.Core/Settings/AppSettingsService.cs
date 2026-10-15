using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.Settings;

/// <summary>Isi satu namespace beserta versinya (hash isi), dipakai klien untuk polling bersyarat (ETag).</summary>
public sealed record AppSettingSnapshot(string Version, IReadOnlyList<AppSetting> Items);

/// <summary>
/// Pengaturan aplikasi per tenant (G6): validasi, CRUD untuk admin, dan snapshot berversi untuk klien. Berjalan di
/// konteks tenant (query filter + RLS); tenant lain tidak pernah terlihat.
/// </summary>
public sealed class AppSettingsService(GatewayDbContext db)
{
    public const int MaxSettingsPerTenant = 200;
    public const int MaxValueLength = 2000;
    public const int MaxDescriptionLength = 200;

    // \z (bukan $): $ menerima newline di akhir.
    private static readonly Regex NamespacePattern = new(@"^[a-z0-9][a-z0-9._-]{0,49}\z", RegexOptions.CultureInvariant);
    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,99}\z", RegexOptions.CultureInvariant);

    public Task<List<AppSetting>> ListAsync(string? ns, CancellationToken ct)
    {
        var query = db.AppSettings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(ns)) query = query.Where(s => s.Namespace == ns);
        return query.OrderBy(s => s.Namespace).ThenBy(s => s.Key).Take(MaxSettingsPerTenant).ToListAsync(ct);
    }

    /// <summary>Buat atau ubah satu pengaturan. Mengembalikan baris dan nilai sebelumnya (null bila baru) untuk audit.</summary>
    public async Task<(AppSetting Setting, string? PreviousValue)> SetAsync(
        string? ns, string? key, string? value, string? valueType, string? description, long? userId, CancellationToken ct)
    {
        ns = RequireNamespace(ns);
        key = RequireKey(key);
        var type = RequireType(valueType);
        var normalized = NormalizeValue(type, value);
        var note = NormalizeDescription(description);

        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Namespace == ns && s.Key == key, ct);
        string? previous = null;
        if (row is null)
        {
            if (await db.AppSettings.CountAsync(ct) >= MaxSettingsPerTenant)
                throw GatewayException.Conflict("setting_limit", $"Maksimal {MaxSettingsPerTenant} pengaturan per tenant.");
            row = new AppSetting { Namespace = ns, Key = key, Value = normalized, ValueType = type, Description = note, UpdatedByUserId = userId };
            db.AppSettings.Add(row);
        }
        else
        {
            previous = row.Value;
            row.Value = normalized;
            row.ValueType = type;
            row.Description = note;
            row.UpdatedByUserId = userId;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await SaveAsync(ct);
        return (row, previous);
    }

    /// <summary>Hapus satu pengaturan; mengembalikan baris yang dihapus (untuk audit).</summary>
    public async Task<AppSetting> DeleteAsync(string? ns, string? key, CancellationToken ct)
    {
        ns = RequireNamespace(ns);
        key = RequireKey(key);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Namespace == ns && s.Key == key, ct)
            ?? throw GatewayException.NotFound("Pengaturan");
        db.AppSettings.Remove(row);
        await SaveAsync(ct);
        return row;
    }

    public async Task<AppSettingSnapshot> SnapshotAsync(string? ns, CancellationToken ct)
    {
        ns = RequireNamespace(ns);
        var items = await db.AppSettings.AsNoTracking().Where(s => s.Namespace == ns).OrderBy(s => s.Key).ToListAsync(ct);
        return new AppSettingSnapshot(VersionOf(items), items);
    }

    /// <summary>16 heks pertama SHA-256 atas (key, tipe, nilai) berurutan: berubah bila ada yang ditambah, diubah, atau dihapus.</summary>
    internal static string VersionOf(IEnumerable<AppSetting> items)
    {
        var text = new StringBuilder();
        foreach (var item in items)
            text.Append(item.Key).Append('\0').Append(item.ValueType).Append('\0').Append(item.Value).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } })
        {
            db.ChangeTracker.Clear();
            throw GatewayException.Conflict("concurrent_update", "Pengaturan diubah pihak lain. Muat ulang lalu coba lagi.");
        }
    }

    private static string RequireNamespace(string? ns) =>
        ns is not null && NamespacePattern.IsMatch(ns)
            ? ns
            : throw GatewayException.BadRequest("invalid_namespace", "Namespace wajib 1-50 karakter: huruf kecil, angka, titik, garis bawah, atau strip.");

    private static string RequireKey(string? key) =>
        key is not null && KeyPattern.IsMatch(key)
            ? key
            : throw GatewayException.BadRequest("invalid_key", "Key wajib 1-100 karakter: huruf, angka, titik, titik dua, garis bawah, atau strip.");

    private static string RequireType(string? type) =>
        type is not null && SettingValueTypes.All.Contains(type)
            ? type
            : throw GatewayException.BadRequest("invalid_value_type", "valueType harus int, bool, atau text.");

    /// <summary>Nilai dibakukan menurut tipe (int: bilangan bulat 32-bit; bool: true/false huruf kecil).</summary>
    private static string NormalizeValue(string type, string? value)
    {
        var text = value ?? "";
        if (text.Length > MaxValueLength)
            throw GatewayException.BadRequest("invalid_value", $"Nilai maksimal {MaxValueLength} karakter.");
        switch (type)
        {
            case SettingValueTypes.Int:
                return int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : throw GatewayException.BadRequest("invalid_value", "Nilai bertipe int harus bilangan bulat 32-bit.");
            case SettingValueTypes.Bool:
                var flag = text.Trim().ToLowerInvariant();
                return flag is "true" or "false"
                    ? flag
                    : throw GatewayException.BadRequest("invalid_value", "Nilai bertipe bool harus true atau false.");
            default:
                return text;
        }
    }

    private static string? NormalizeDescription(string? description)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= MaxDescriptionLength
            ? text
            : throw GatewayException.BadRequest("invalid_description", $"Deskripsi maksimal {MaxDescriptionLength} karakter.");
    }
}
