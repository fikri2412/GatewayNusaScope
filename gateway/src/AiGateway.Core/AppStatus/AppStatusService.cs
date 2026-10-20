using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiGateway.Core.Common;
using AiGateway.Core.Data;
using AiGateway.Core.Domain;
using AiGateway.Core.Proxy;
using AiGateway.Core.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Core.AppStatus;

/// <summary>Body <c>POST /client/api/app-status</c>: laporan ringan aplikasi klien (camelCase).</summary>
public sealed record AppStatusReportInput(
    string? Namespace,
    string? SourceSettingsVersion,
    string? PolicyHash,
    string? PromptHash,
    Dictionary<string, string?>? EffectiveValues,
    IReadOnlyList<AppStatusSupportedKeyInput>? SupportedKeys,
    string? SyncStatus,
    DateTimeOffset? LastConfigSuccessAt,
    IReadOnlyList<string?>? RejectedKeys,
    IReadOnlyList<string?>? IgnoredKeys,
    string? Reason,
    AppStatusCountsInput? Queue,
    AppStatusCountsInput? Jobs,
    IReadOnlyList<AppStatusPolicyGroupInput>? ActivePolicies,
    int? ReportIntervalSeconds,
    DateTimeOffset? ObservedAt);

public sealed record AppStatusSupportedKeyInput(
    string? Key, string? ValueType, int? Min, int? Max, int? MaxLength,
    IReadOnlyList<string?>? AllowedValues, string? Description, bool? Redacted);

public sealed record AppStatusCountsInput(long? Pending, long? Processing, long? Done, long? Failed);

public sealed record AppStatusPolicyGroupInput(string? PolicyHash, long? QueueItems, long? Jobs);

/// <summary>Jawaban <c>POST /client/api/app-status</c>: jam server saat laporan disimpan.</summary>
public sealed record AppStatusReceivedDto(DateTime ReceivedAt);

public sealed record AppStatusSupportedKeyDto(
    string Key, string ValueType, int? Min, int? Max, int? MaxLength,
    IReadOnlyList<string>? AllowedValues, string? Description, bool Redacted);

public sealed record AppStatusCountsDto(long Pending, long Processing, long Done, long Failed);

public sealed record AppStatusPolicyGroupDto(string PolicyHash, long QueueItems, long Jobs);

/// <summary>Satu laporan status beserta versi pengaturan gateway yang berlaku saat dibaca.</summary>
public sealed record AppStatusReportDto(
    long Id, Guid? ApiKeyId, string? ApiKeyName, string Namespace, string? ConfiguredVersion, DateTime ReceivedAt, bool Stale,
    string? SourceSettingsVersion, string PolicyHash, string PromptHash,
    IReadOnlyDictionary<string, string> EffectiveValues, IReadOnlyList<AppStatusSupportedKeyDto> SupportedKeys,
    string SyncStatus, DateTime? LastConfigSuccessAt, IReadOnlyList<string> RejectedKeys, IReadOnlyList<string> IgnoredKeys,
    string? Reason, AppStatusCountsDto Queue, AppStatusCountsDto Jobs, IReadOnlyList<AppStatusPolicyGroupDto> ActivePolicies,
    int ReportIntervalSeconds, DateTime ObservedAt);

/// <summary>Jawaban <c>GET /admin/api/app-status</c>.</summary>
public sealed record AppStatusListDto(string? Namespace, string? ConfiguredVersion, IReadOnlyList<AppStatusReportDto> Items);

/// <summary>
/// Laporan status aplikasi klien (monitoring lanjutan G6): validasi allowlist, upsert satu baris per API key+namespace,
/// dan pembacaan untuk admin. Tenant/project/key selalu dari hasil autentikator, tidak pernah dari body. Laporan tidak
/// pernah berisi prompt, secret, atau path; nilai yang tidak dikenal ditolak, bukan disimpan.
/// </summary>
public sealed class AppStatusService(GatewayDbContext db, AppSettingsService settings, TimeProvider clock)
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxEffectiveValues = 32;
    public const int MaxSupportedKeys = 128;
    public const int MaxKeyLists = 32;
    public const int MaxActivePolicies = 128;
    public const int MaxReports = 200;
    public const int MaxReasonLength = 200;
    public const int MaxControlValueLength = 64;
    public const int MaxAllowedValues = 32;
    public const int MaxAllowedValueLength = 50;
    public const int MinReportIntervalSeconds = 1;
    public const int MaxReportIntervalSeconds = 3600;

    // \z (bukan $): $ menerima newline di akhir. Namespace/key mengikuti aturan pengaturan G6.
    private static readonly Regex NamespacePattern = new(@"^[a-z0-9][a-z0-9._-]{0,49}\z", RegexOptions.CultureInvariant);
    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,99}\z", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant);

    private sealed record Control(string ValueType, int Min, int Max, string[]? AllowedValues);

    /// <summary>
    /// Allowlist satu-satunya key yang boleh masuk <c>effectiveValues</c>, dengan batas yang sama seperti AppOptions
    /// chatbot. Prompt, secret, DLL/storage/connection path, dan key lain tidak bisa dilaporkan di sini.
    /// </summary>
    private static readonly Dictionary<string, Control> Controls = new(StringComparer.Ordinal)
    {
        ["Operator:MaxToolLoops"] = new(SettingValueTypes.Int, 1, 20, null),
        ["Operator:MaxHistoryMessages"] = new(SettingValueTypes.Int, 1, 500, null),
        ["Operator:MaxTokensPerConversation"] = new(SettingValueTypes.Int, 1000, int.MaxValue, null),
        ["Queue:MaxRetries"] = new(SettingValueTypes.Int, 1, 20, null),
        ["Queue:StaleLockMinutes"] = new(SettingValueTypes.Int, 1, 1440, null),
        ["Ai:MaxTokens"] = new(SettingValueTypes.Int, 1, 64000, null),
        ["Response:Detail"] = new(SettingValueTypes.Text, 0, 0, ["ringkas", "normal", "mendalam"]),
        ["Operator:FileTools"] = new(SettingValueTypes.Text, 0, 0, ["auto", "selalu"]),
        ["Dispatcher:MaxConcurrentOperators"] = new(SettingValueTypes.Int, 1, 32, null),
        ["Dispatcher:MaxConcurrentSpecialists"] = new(SettingValueTypes.Int, 1, 16, null),
        ["Dispatcher:SweepSeconds"] = new(SettingValueTypes.Int, 1, 300, null),
        ["Ai:AttemptTimeoutSeconds"] = new(SettingValueTypes.Int, 1, 600, null),
        ["Ai:TotalRequestTimeoutSeconds"] = new(SettingValueTypes.Int, 1, 3600, null),
        ["Ai:StreamFirstByteSeconds"] = new(SettingValueTypes.Int, 1, 300, null),
        ["Ai:StreamIdleSeconds"] = new(SettingValueTypes.Int, 1, 3600, null),
        ["Ai:MaxStreamSeconds"] = new(SettingValueTypes.Int, 1, 86400, null),
        ["Ai:MaxStreamBytes"] = new(SettingValueTypes.Int, 1, 8 * 1024 * 1024, null),
        ["Ai:MaxStreamEventBytes"] = new(SettingValueTypes.Int, 1, 256 * 1024, null),
        ["Upload:MaxBytes"] = new(SettingValueTypes.Int, 1, 10 * 1024 * 1024, null),
        ["Upload:AllowedExtensions"] = new(SettingValueTypes.Text, 0, 0, null),
        ["Upload:MaxAttachmentsPerMessage"] = new(SettingValueTypes.Int, 1, 6, null),
        ["Document:MaxPages"] = new(SettingValueTypes.Int, 1, 100, null),
        ["Document:MaxExtractedChars"] = new(SettingValueTypes.Int, 1, 100_000, null),
        ["Document:MaxImagePixels"] = new(SettingValueTypes.Int, 1, 16_000_000, null),
        ["Document:VisionMaxLongEdge"] = new(SettingValueTypes.Int, 64, 1280, null),
        ["Document:VisionMinLongEdge"] = new(SettingValueTypes.Int, 64, 1280, null),
        ["Document:VisionMaxImageBytes"] = new(SettingValueTypes.Int, 1024, 400_000, null),
        ["Document:VisionMaxImagesPerRequest"] = new(SettingValueTypes.Int, 1, 4, null),
        ["Document:VisionMaxBase64CharsPerRequest"] = new(SettingValueTypes.Int, 1368, 2_000_000, null),
        ["Document:WatchdogSeconds"] = new(SettingValueTypes.Int, 1, 60, null),
    };

    /// <summary>
    /// Hanya boleh muncul sebagai metadata di <c>supportedKeys</c>: teks tambahan admin tidak pernah dikirim sebagai
    /// nilai (tetap di Pengaturan G6 yang punya RBAC dan audit sendiri).
    /// </summary>
    private const string AdditionalInstructionsKey = "Prompt:AdditionalInstructions";

    private static GatewayException Invalid(string field) =>
        GatewayException.BadRequest("invalid_report", $"Laporan status tidak valid: {field}.");

    /// <summary>Simpan laporan sebagai satu baris per key+namespace; mengembalikan jam server saat diterima.</summary>
    public async Task<DateTime> UpsertAsync(GatewayCaller caller, AppStatusReportInput input, CancellationToken ct)
    {
        var report = Validate(input);
        var now = clock.GetUtcNow().UtcDateTime;
        var row = await db.AppStatusReports
            .FirstOrDefaultAsync(r => r.ApiKeyId == caller.Key.Id && r.Namespace == report.Namespace, ct);
        if (row is null)
        {
            row = new AppStatusReport
            {
                Namespace = report.Namespace,
                ApiKeyId = caller.Key.Id,
                ProjectId = caller.ProjectId,
                PolicyHash = report.PolicyHash,
                PromptHash = report.PromptHash,
                EffectiveValuesJson = report.EffectiveValuesJson,
                SupportedKeysJson = report.SupportedKeysJson,
                SyncStatus = report.SyncStatus,
                RejectedKeysJson = report.RejectedKeysJson,
                IgnoredKeysJson = report.IgnoredKeysJson,
                ActivePoliciesJson = report.ActivePoliciesJson,
            };
            db.AppStatusReports.Add(row);
        }

        Apply(row, report, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Dua laporan pertama untuk key+namespace yang sama berbarengan: satu insert kalah index unik.
            // Ulangi sekali sebagai update baris pemenang; konflik kedua tidak ditelan.
            db.ChangeTracker.Clear();
            var existing = await db.AppStatusReports
                .FirstOrDefaultAsync(r => r.ApiKeyId == caller.Key.Id && r.Namespace == report.Namespace, ct);
            if (existing is null) throw;
            Apply(existing, report, now);
            await db.SaveChangesAsync(ct);
        }
        return now;
    }

    /// <summary>Baca laporan tenant ini, difilter satu namespace bila diminta; versi configured dihitung saat dibaca.</summary>
    public async Task<AppStatusListDto> ListAsync(string? ns, CancellationToken ct)
    {
        var requested = string.IsNullOrWhiteSpace(ns) ? null : RequireNamespace(ns);
        var rows = await db.AppStatusReports.AsNoTracking()
            .Where(r => requested == null || r.Namespace == requested)
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id)
            .Take(MaxReports)
            .ToListAsync(ct);

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (requested is not null)
        {
            versions[requested] = (await settings.SnapshotAsync(requested, ct)).Version;
        }
        else
        {
            foreach (var name in rows.Select(r => r.Namespace).Distinct(StringComparer.Ordinal))
                versions[name] = (await settings.SnapshotAsync(name, ct)).Version;
        }

        var keyIds = rows.Select(r => r.ApiKeyId).Distinct().ToList();
        var keys = await db.ApiKeys.AsNoTracking().Where(k => keyIds.Contains(k.Id))
            .Select(k => new { k.Id, k.PublicId, k.Name }).ToListAsync(ct);
        var byId = keys.ToDictionary(k => k.Id);
        var now = clock.GetUtcNow().UtcDateTime;
        var items = new List<AppStatusReportDto>(rows.Count);
        foreach (var r in rows)
        {
            byId.TryGetValue(r.ApiKeyId, out var key);
            items.Add(new AppStatusReportDto(
                r.Id, key?.PublicId, key?.Name, r.Namespace, versions[r.Namespace], r.ReceivedAt,
                IsStale(r.ReceivedAt, r.ReportIntervalSeconds, now), r.SourceSettingsVersion, r.PolicyHash, r.PromptHash,
                Deserialize<Dictionary<string, string>>(r.EffectiveValuesJson) ?? new Dictionary<string, string>(),
                Deserialize<List<AppStatusSupportedKeyDto>>(r.SupportedKeysJson) ?? [],
                r.SyncStatus, r.LastConfigSuccessAt,
                Deserialize<List<string>>(r.RejectedKeysJson) ?? [], Deserialize<List<string>>(r.IgnoredKeysJson) ?? [],
                r.Reason,
                new AppStatusCountsDto(r.QueuePending, r.QueueProcessing, r.QueueDone, r.QueueFailed),
                new AppStatusCountsDto(r.JobsPending, r.JobsProcessing, r.JobsDone, r.JobsFailed),
                Deserialize<List<AppStatusPolicyGroupDto>>(r.ActivePoliciesJson) ?? [],
                r.ReportIntervalSeconds, r.ObservedAt));
        }

        return new AppStatusListDto(requested, requested is null ? null : versions[requested], items);
    }

    /// <summary>Segar bila selisih jam server dengan receivedAt belum melewati max(60 dtk, 3x interval laporan).</summary>
    public static bool IsStale(DateTime receivedAt, int reportIntervalSeconds, DateTime now) =>
        now - receivedAt > TimeSpan.FromSeconds(Math.Max(60, reportIntervalSeconds * 3));

    private sealed record ValidatedReport(
        string Namespace, string? SourceSettingsVersion, string PolicyHash, string PromptHash,
        string EffectiveValuesJson, string SupportedKeysJson, string SyncStatus, DateTime? LastConfigSuccessAt,
        string RejectedKeysJson, string IgnoredKeysJson, string? Reason,
        AppStatusCountsDto Queue, AppStatusCountsDto Jobs, string ActivePoliciesJson,
        int ReportIntervalSeconds, DateTime ObservedAt);

    private static ValidatedReport Validate(AppStatusReportInput input)
    {
        var ns = RequireNamespace(input.Namespace);
        var policyHash = RequireHash(input.PolicyHash, "policyHash");
        var promptHash = RequireHash(input.PromptHash, "promptHash");
        var sourceVersion = NormalizeVersion(input.SourceSettingsVersion);
        var syncStatus = input.SyncStatus is { } status && AppStatusSyncStatuses.All.Contains(status)
            ? status
            : throw Invalid("syncStatus");
        var queue = RequireCounts(input.Queue, "queue");
        var jobs = RequireCounts(input.Jobs, "jobs");
        var interval = input.ReportIntervalSeconds is { } value && value is >= MinReportIntervalSeconds and <= MaxReportIntervalSeconds
            ? value
            : throw Invalid("reportIntervalSeconds");
        if (input.ObservedAt is not { } observedAt) throw Invalid("observedAt");

        return new ValidatedReport(
            ns, sourceVersion, policyHash, promptHash,
            JsonSerializer.Serialize(NormalizeEffectiveValues(input.EffectiveValues), JsonSerializerOptions.Web),
            JsonSerializer.Serialize(NormalizeSupportedKeys(input.SupportedKeys), JsonSerializerOptions.Web),
            syncStatus, input.LastConfigSuccessAt is { } success ? RequireUtc(success, "lastConfigSuccessAt") : null,
            JsonSerializer.Serialize(NormalizeKeyList(input.RejectedKeys, "rejectedKeys"), JsonSerializerOptions.Web),
            JsonSerializer.Serialize(NormalizeKeyList(input.IgnoredKeys, "ignoredKeys"), JsonSerializerOptions.Web),
            NormalizeReason(input.Reason), queue, jobs,
            JsonSerializer.Serialize(NormalizeActivePolicies(input.ActivePolicies), JsonSerializerOptions.Web),
            interval, RequireUtc(observedAt, "observedAt"));
    }

    private static void Apply(AppStatusReport row, ValidatedReport report, DateTime receivedAt)
    {
        row.SourceSettingsVersion = report.SourceSettingsVersion;
        row.PolicyHash = report.PolicyHash;
        row.PromptHash = report.PromptHash;
        row.EffectiveValuesJson = report.EffectiveValuesJson;
        row.SupportedKeysJson = report.SupportedKeysJson;
        row.SyncStatus = report.SyncStatus;
        row.LastConfigSuccessAt = report.LastConfigSuccessAt;
        row.RejectedKeysJson = report.RejectedKeysJson;
        row.IgnoredKeysJson = report.IgnoredKeysJson;
        row.Reason = report.Reason;
        row.QueuePending = report.Queue.Pending;
        row.QueueProcessing = report.Queue.Processing;
        row.QueueDone = report.Queue.Done;
        row.QueueFailed = report.Queue.Failed;
        row.JobsPending = report.Jobs.Pending;
        row.JobsProcessing = report.Jobs.Processing;
        row.JobsDone = report.Jobs.Done;
        row.JobsFailed = report.Jobs.Failed;
        row.ActivePoliciesJson = report.ActivePoliciesJson;
        row.ReportIntervalSeconds = report.ReportIntervalSeconds;
        row.ObservedAt = report.ObservedAt;
        row.ReceivedAt = receivedAt;
    }

    private static Dictionary<string, string> NormalizeEffectiveValues(Dictionary<string, string?>? values)
    {
        // Ordinal: key yang hanya beda huruf besar/kecil tidak boleh menyatu diam-diam jadi satu nilai.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is null) return result;
        if (values.Count > MaxEffectiveValues) throw Invalid("effectiveValues");
        foreach (var (key, value) in values)
        {
            if (!Controls.TryGetValue(key, out var control)) throw Invalid("effectiveValues (key di luar allowlist)");
            if (value is not { } text || text.Length > MaxControlValueLength) throw Invalid("effectiveValues (nilai)");
            result[key] = key == "Upload:AllowedExtensions" ? CanonicalExtensions(text)
                : control.ValueType == SettingValueTypes.Int ? CanonicalInt(text, control) : CanonicalChoice(text, control);
        }
        return result;
    }

    private static string CanonicalInt(string text, Control control) =>
        int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
        && value >= control.Min && value <= control.Max
            ? value.ToString(CultureInfo.InvariantCulture)
            : throw Invalid("effectiveValues (di luar batas)");

    private static string CanonicalChoice(string text, Control control)
    {
        var value = text.Trim().ToLowerInvariant();
        return control.AllowedValues!.Contains(value) ? value : throw Invalid("effectiveValues (nilai tidak diizinkan)");
    }

    private static string CanonicalExtensions(string text)
    {
        var extensions = text.Split(',', StringSplitOptions.TrimEntries)
            .Select(extension => extension.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
        if (extensions.Length is < 1 or > 7
            || extensions.Any(extension => extension is not (".xlsx" or ".csv" or ".pdf" or ".png" or ".jpg" or ".jpeg" or ".webp")))
            throw Invalid("effectiveValues (ekstensi tidak didukung)");
        for (var i = 1; i < extensions.Length; i++)
            if (extensions[i] == extensions[i - 1])
                throw Invalid("effectiveValues (ekstensi tidak didukung)");
        return string.Join(',', extensions);
    }

    private static List<AppStatusSupportedKeyDto> NormalizeSupportedKeys(IReadOnlyList<AppStatusSupportedKeyInput>? keys)
    {
        var result = new List<AppStatusSupportedKeyDto>();
        if (keys is null) return result;
        if (keys.Count > MaxSupportedKeys) throw Invalid("supportedKeys");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key.Key is not { } name || !KeyPattern.IsMatch(name)
                || (!Controls.ContainsKey(name) && name != AdditionalInstructionsKey))
                throw Invalid("supportedKeys.key");
            if (!seen.Add(name)) throw Invalid("supportedKeys (key duplikat)");
            if (key.ValueType is not { } type || !SettingValueTypes.All.Contains(type)) throw Invalid("supportedKeys.valueType");
            if (key.Min is { } min && key.Max is { } max && min > max) throw Invalid("supportedKeys (min > max)");
            if (key.MaxLength is { } maxLength && maxLength is < 1 or > AppSettingsService.MaxValueLength)
                throw Invalid("supportedKeys.maxLength");
            IReadOnlyList<string>? allowed = null;
            if (key.AllowedValues is { } values)
            {
                if (values.Count > MaxAllowedValues) throw Invalid("supportedKeys.allowedValues");
                if (values.Any(v => string.IsNullOrEmpty(v) || v.Length > MaxAllowedValueLength))
                    throw Invalid("supportedKeys.allowedValues");
                allowed = values.Select(v => v!).ToList();
            }
            var description = string.IsNullOrWhiteSpace(key.Description) ? null : key.Description.Trim();
            if (description is not null && description.Length > AppSettingsService.MaxDescriptionLength)
                throw Invalid("supportedKeys.description");
            result.Add(new AppStatusSupportedKeyDto(name, type, key.Min, key.Max, key.MaxLength, allowed, description, key.Redacted ?? false));
        }
        return result;
    }

    private static List<string> NormalizeKeyList(IReadOnlyList<string?>? keys, string field)
    {
        var result = new List<string>();
        if (keys is null) return result;
        if (keys.Count > MaxKeyLists) throw Invalid(field);
        foreach (var key in keys)
        {
            if (key is null || !KeyPattern.IsMatch(key)) throw Invalid(field);
            result.Add(key);
        }
        return result;
    }

    private static List<AppStatusPolicyGroupDto> NormalizeActivePolicies(IReadOnlyList<AppStatusPolicyGroupInput>? groups)
    {
        var result = new List<AppStatusPolicyGroupDto>();
        if (groups is null) return result;
        if (groups.Count > MaxActivePolicies) throw Invalid("activePolicies");
        foreach (var group in groups)
        {
            if (group.PolicyHash is not { } hash || !HashPattern.IsMatch(hash)) throw Invalid("activePolicies.policyHash");
            result.Add(new AppStatusPolicyGroupDto(hash,
                RequireCount(group.QueueItems, "activePolicies.queueItems"), RequireCount(group.Jobs, "activePolicies.jobs")));
        }
        return result;
    }

    private static AppStatusCountsDto RequireCounts(AppStatusCountsInput? counts, string field) =>
        counts is null
            ? throw Invalid($"{field} wajib diisi")
            : new AppStatusCountsDto(
                RequireCount(counts.Pending, $"{field}.pending"), RequireCount(counts.Processing, $"{field}.processing"),
                RequireCount(counts.Done, $"{field}.done"), RequireCount(counts.Failed, $"{field}.failed"));

    private static long RequireCount(long? value, string field) =>
        value is { } count && count >= 0 ? count : throw Invalid(field);

    private static string? NormalizeReason(string? reason)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= MaxReasonLength ? text : throw Invalid("reason");
    }

    private static string? NormalizeVersion(string? version) =>
        version is null ? null : VersionPattern.IsMatch(version) ? version : throw Invalid("sourceSettingsVersion");

    private static DateTime RequireUtc(DateTimeOffset value, string field)
    {
        var utc = value.UtcDateTime;
        // Batas kewarasan saja; kesegaran tetap ditentukan receivedAt jam server, bukan jam klien.
        if (utc.Year is < 2000 or > 2100) throw Invalid(field);
        return utc;
    }

    private static string RequireHash(string? hash, string field) =>
        hash is not null && HashPattern.IsMatch(hash) ? hash : throw Invalid(field);

    private static string RequireNamespace(string? ns) =>
        ns is not null && NamespacePattern.IsMatch(ns)
            ? ns
            : throw GatewayException.BadRequest("invalid_namespace", "Namespace wajib 1-50 karakter: huruf kecil, angka, titik, garis bawah, atau strip.");

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web);
}
