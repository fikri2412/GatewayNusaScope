using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Shared.Options;

public sealed class SettingsOptions
{
    public const string Section = "Settings";
    [Range(1, 3600)] public int RefreshSeconds { get; set; } = 15;
}

/// <summary>
/// Sumber konfigurasi terakhir untuk namespace <c>chatbot</c>: hanya kontrol pada allowlist yang boleh ditimpa,
/// snapshot yang tidak valid ditolak utuh (nilai berlaku dan ETag tidak berubah), dan penghapusan nilai kembali ke
/// konfigurasi lokal yang dikunci saat startup. Provider juga memegang <see cref="CurrentPolicy"/> immutable yang
/// dibaca API sekali per pesan, serta menyusun laporan status untuk gateway.
/// </summary>
public sealed class GatewaySettingsProvider : ConfigurationProvider, IConfigurationSource
{
    /// <summary>Namespace pengaturan sekaligus namespace laporan status.</summary>
    public const string SettingsNamespace = "chatbot";

    private const int MaxRemoteItems = 200;            // sama dengan batas pengaturan gateway per tenant
    private const int MaxReportedKeys = 32;            // gateway: batas rejectedKeys/ignoredKeys
    private const int MaxReportedActivePolicies = 128; // gateway: batas activePolicies
    private const int MaxReasonLength = 200;           // gateway: batas reason

    private const string LocalStatus = "local";
    private const string SyncedStatus = "synced";
    private const string RejectedStatus = "rejected";
    private const string LastKnownGoodStatus = "last-known-good";

    // \z (bukan $): $ menerima newline di akhir. Sama seperti pola gateway.
    private static readonly Regex VersionPattern = new(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex KeyNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,99}\z", RegexOptions.CultureInvariant);

    private sealed record Setting(string Key, string Value, string ValueType);
    private sealed record Snapshot(string Version, List<Setting> Items);

    /// <summary>Satu kontrol yang boleh ditimpa gateway; batas harus sama dengan atribut di AppOptions.</summary>
    private sealed record Control(string Key, string ValueType, int? Min = null, int? Max = null,
        int? MaxLength = null, string[]? AllowedValues = null, bool Required = false);

    /// <summary>Nilai efektif kandidat yang lolos: baseline lokal yang sudah ditimpa nilai gateway.</summary>
    private sealed record Controls(int MaxToolLoops, int MaxHistoryMessages, int MaxTokensPerConversation, int MaxRetries,
        int StaleLockMinutes, int? MaxOutputTokens, string Detail, int MaxConcurrentOperators,
        int MaxConcurrentSpecialists, int SweepSeconds, string OperatorDll, string SpecialistDll,
        string AdditionalInstructions, AiRuntimeLimits Ai, UploadRuntimeLimits Upload, DocumentRuntimeLimits Document);

    /// <summary>Seluruh status dibawa satu referensi immutable supaya pembaca tidak pernah melihat state campuran.</summary>
    private sealed record State(Controls Controls, ExecutionPolicy Policy, string SyncStatus,
        DateTimeOffset? LastConfigSuccessAt, IReadOnlyList<string> RejectedKeys, IReadOnlyList<string> IgnoredKeys,
        string? Reason);

    /// <summary>Kandidat yang dikenal tapi tidak lolos; alasannya tidak pernah memuat isi nilai kandidat.</summary>
    private sealed record Rejection(string Reason, IReadOnlyList<string> Keys, IReadOnlyList<string> Ignored);

    private sealed record Validated(Dictionary<string, string?> Data, Controls Controls, List<string> Ignored);

    /// <summary>Kontrol yang nilainya boleh terlihat di telemetri gateway (effectiveValues + supportedKeys).</summary>
    private static readonly Control[] ValueControls =
    [
        new("Operator:MaxToolLoops", "int", 1, 20),
        new("Operator:MaxHistoryMessages", "int", 1, 500),
        new("Operator:MaxTokensPerConversation", "int", 1000, int.MaxValue),
        new("Queue:MaxRetries", "int", 1, 20),
        new("Queue:StaleLockMinutes", "int", 1, 1440),
        new("Ai:MaxTokens", "int", 1, 64000),
        new("Response:Detail", "text", AllowedValues: ["ringkas", "normal", "mendalam"]),
        new("Dispatcher:MaxConcurrentOperators", "int", 1, 32),
        new("Dispatcher:MaxConcurrentSpecialists", "int", 1, 16),
        new("Dispatcher:SweepSeconds", "int", 1, 300),
        new("Ai:AttemptTimeoutSeconds", "int", 1, RuntimeSafetyLimits.AiAttemptSeconds),
        new("Ai:TotalRequestTimeoutSeconds", "int", 1, RuntimeSafetyLimits.AiTotalSeconds),
        new("Ai:StreamFirstByteSeconds", "int", 1, RuntimeSafetyLimits.StreamFirstByteSeconds),
        new("Ai:StreamIdleSeconds", "int", 1, RuntimeSafetyLimits.StreamIdleSeconds),
        new("Ai:MaxStreamSeconds", "int", 1, RuntimeSafetyLimits.StreamSeconds),
        new("Ai:MaxStreamBytes", "int", 1, RuntimeSafetyLimits.StreamBytes),
        new("Ai:MaxStreamEventBytes", "int", 1, RuntimeSafetyLimits.StreamEventBytes),
        new("Upload:MaxBytes", "int", 1, RuntimeSafetyLimits.UploadBytes),
        new("Upload:AllowedExtensions", "text", MaxLength: 128, Required: true),
        new("Upload:MaxAttachmentsPerMessage", "int", 1, RuntimeSafetyLimits.AttachmentsPerMessage),
        new("Document:MaxPages", "int", 1, RuntimeSafetyLimits.PdfPages),
        new("Document:MaxExtractedChars", "int", 1, RuntimeSafetyLimits.ExtractedChars),
        new("Document:MaxImagePixels", "int", 1, RuntimeSafetyLimits.ImagePixels),
        new("Document:VisionMaxLongEdge", "int", 64, RuntimeSafetyLimits.VisionLongEdge),
        new("Document:VisionMinLongEdge", "int", 64, RuntimeSafetyLimits.VisionLongEdge),
        new("Document:VisionMaxImageBytes", "int", 1024, RuntimeSafetyLimits.VisionImageBytes),
        new("Document:VisionMaxImagesPerRequest", "int", 1, RuntimeSafetyLimits.VisionBatchImages),
        new("Document:VisionMaxBase64CharsPerRequest", "int", 1368, RuntimeSafetyLimits.VisionBase64Chars),
        new("Document:WatchdogSeconds", "int", 1, RuntimeSafetyLimits.DocumentWatchdogSeconds),
    ];

    /// <summary>
    /// Kontrol yang boleh ditimpa gateway: telemetri + path DLL dispatcher (semantik lama) + instruksi tambahan
    /// admin. Nilai DLL dan prompt tidak pernah ikut ke laporan; prompt hanya metadata redacted.
    /// </summary>
    private static readonly Control[] AppliedControls =
    [
        .. ValueControls,
        new("Dispatcher:OperatorDll", "text", Required: true),
        new("Dispatcher:SpecialistDll", "text", Required: true),
        new("Prompt:AdditionalInstructions", "text", MaxLength: 2000),
    ];

    private static readonly IReadOnlyList<AppSettingMetadata> SupportedKeys =
    [
        .. ValueControls.Select(c => new AppSettingMetadata(c.Key, c.ValueType, c.Min, c.Max, c.MaxLength, c.AllowedValues)),
        new AppSettingMetadata("Prompt:AdditionalInstructions", "text", null, null, 2000, null, null, Redacted: true),
    ];

    private readonly Controls _localControls;
    private string? _etag;
    private volatile State _state;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    /// <summary>Baseline lokal dibaca sebelum provider dipasang ke konfigurasi, lalu tidak pernah berubah lagi.</summary>
    public GatewaySettingsProvider(IConfiguration local)
    {
        _localControls = ReadLocalControls(local);
        _state = new State(_localControls, BuildPolicy(_localControls, null), LocalStatus, null, [], [], null);
    }

    /// <summary>
    /// Snapshot immutable kebijakan yang berlaku; API cukup membacanya sekali per pesan. Nilainya selalu yang
    /// terakhir diterima gateway, atau baseline lokal bila belum ada snapshot yang lolos.
    /// </summary>
    public ExecutionPolicy CurrentPolicy => _state.Policy;

    /// <summary>
    /// Dipanggil berurutan oleh poller. Snapshot tidak dipublikasikan sampai seluruh nilai lolos validasi;
    /// kandidat yang tidak lolos tidak mengubah nilai berlaku maupun ETag, tetapi dicatat sebagai status
    /// <c>rejected</c> agar admin melihat key yang salah.
    /// </summary>
    public async Task<bool> RefreshAsync(HttpClient http, CancellationToken ct)
    {
        Rejection? rejection = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/client/api/settings?namespace={SettingsNamespace}");
            if (_etag is not null) request.Headers.IfNoneMatch.ParseAdd(_etag);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                // Isi gateway masih sama dengan yang berlaku: konfirmasi sukses tanpa membaca ulang nilai.
                _state = _state with
                {
                    SyncStatus = SyncedStatus, RejectedKeys = [], Reason = null, LastConfigSuccessAt = DateTimeOffset.UtcNow,
                };
                return false;
            }
            response.EnsureSuccessStatusCode();

            var snapshot = await ReadSnapshotAsync(response, ct);
            var validated = Validate(snapshot, out rejection);
            if (validated is null)
                throw new InvalidDataException(rejection!.Reason);

            var changed = validated.Data.Count != Data.Count
                || validated.Data.Any(p => !Data.TryGetValue(p.Key, out var old) || old != p.Value);
            var policy = BuildPolicy(validated.Controls, snapshot.Version);
            _etag = response.Headers.ETag?.ToString() ?? new EntityTagHeaderValue($"\"{snapshot.Version}\"").ToString();
            // Kebijakan berlaku dulu, baru konfigurasi: pesan baru tidak pernah memakai kebijakan lama setelah dispatcher berubah.
            _state = new State(validated.Controls, policy, SyncedStatus, DateTimeOffset.UtcNow, [], validated.Ignored, null);
            if (!changed) return false;
            Data = validated.Data;
            OnReload();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Kandidat tidak valid ditolak (reason hanya memuat nama key), kegagalan lain berarti nilai terakhir
            // masih berlaku. Pesan tidak pernah memuat isi kandidat, prompt, atau body respons gateway.
            var rejected = rejection ?? (ex is InvalidDataException ? new Rejection(Bound(ex.Message), [], []) : null);
            _state = rejected is not null
                ? _state with
                {
                    SyncStatus = RejectedStatus, Reason = rejected.Reason,
                    RejectedKeys = rejected.Keys, IgnoredKeys = rejected.Ignored,
                }
                : _state with
                {
                    SyncStatus = _state.LastConfigSuccessAt is null ? LocalStatus : LastKnownGoodStatus,
                    Reason = Bound(ex.Message),
                };
            throw;
        }
    }

    /// <summary>
    /// Laporan wire <c>POST /client/api/app-status</c> (camelCase) dari status yang berlaku dan hitungan antrean
    /// nyata; hanya memuat kontrol allowlist dan tidak pernah memuat prompt, secret, path DLL, atau hitungan karangan.
    /// </summary>
    public AppStatusReport CreateReport(QueueRuntimeSnapshot runtime, int reportIntervalSeconds, DateTimeOffset observedAt)
    {
        var state = _state;
        // Laporan yang kepanjangan ditolak utuh: sebagian daftar kebijakan aktif bukan gambaran lengkap.
        if (runtime.ActivePolicies.Count > MaxReportedActivePolicies)
            throw new InvalidDataException("Hitungan kebijakan aktif melebihi batas laporan gateway.");
        return new AppStatusReport(
            Namespace: SettingsNamespace,
            SourceSettingsVersion: state.Policy.SourceSettingsVersion,
            PolicyHash: state.Policy.PolicyHash,
            PromptHash: state.Policy.PromptHash,
            EffectiveValues: EffectiveValues(state.Controls),
            SupportedKeys: SupportedKeys,
            SyncStatus: state.SyncStatus,
            LastConfigSuccessAt: state.LastConfigSuccessAt,
            RejectedKeys: state.RejectedKeys,
            IgnoredKeys: state.IgnoredKeys,
            Reason: state.Reason,
            Queue: runtime.Queue,
            Jobs: runtime.Jobs,
            ActivePolicies: runtime.ActivePolicies,
            ReportIntervalSeconds: reportIntervalSeconds,
            ObservedAt: observedAt);
    }

    private static async Task<Snapshot> ReadSnapshotAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Snapshot? snapshot;
        try
        {
            snapshot = await response.Content.ReadFromJsonAsync<Snapshot>(ct);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Body snapshot pengaturan bukan JSON yang dikenal.");
        }
        return snapshot ?? throw new InvalidDataException("Body snapshot pengaturan kosong.");
    }

    /// <summary>Mengembalikan kandidat yang lolos, atau <c>null</c> dengan seluruh penolakan di <paramref name="rejection"/>.</summary>
    private Validated? Validate(Snapshot snapshot, out Rejection? rejection)
    {
        rejection = null;
        if (snapshot.Items is null || snapshot.Items.Count > MaxRemoteItems)
        {
            rejection = new Rejection("Snapshot pengaturan tidak valid.", [], []);
            return null;
        }
        if (snapshot.Version is null || !VersionPattern.IsMatch(snapshot.Version))
        {
            rejection = new Rejection("Versi snapshot pengaturan tidak valid.", [], []);
            return null;
        }

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var controls = _localControls;
        var rejected = new List<string>();
        var rejectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignored = new List<string>();
        var ignoredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in snapshot.Items)
        {
            if (item is null || item.Key is null || item.Value is null || item.ValueType is null)
            {
                rejection = new Rejection("Item pengaturan tidak lengkap.", [], []);
                return null;
            }
            var control = AppliedControls.FirstOrDefault(c => c.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
            if (control is null)
            {
                // Key di luar allowlist tidak pernah ditimpa; hanya nama yang aman dan pendek yang dilaporkan.
                if (KeyNamePattern.IsMatch(item.Key) && ignoredSet.Add(item.Key)) ignored.Add(item.Key);
                continue;
            }
            if (!seen.Add(control.Key) || !TryCanonicalValue(control, item.ValueType, item.Value, out var canonical))
            {
                if (rejectedSet.Add(control.Key)) rejected.Add(control.Key);
                continue;
            }
            data[control.Key] = canonical;
            controls = ApplyControl(controls, control.Key, canonical);
        }

        if (rejected.Count > 0)
        {
            rejection = new Rejection(RejectionReason(rejected), Capped(rejected), Capped(ignored));
            return null;
        }
        try
        {
            controls.Ai.Validate();
            controls.Upload.Validate();
            controls.Document.Validate();
        }
        catch (ValidationException ex)
        {
            rejection = new Rejection(Bound(ex.Message), Capped([.. data.Keys]), Capped(ignored));
            return null;
        }
        return new Validated(data, controls, Capped(ignored));
    }

    private ExecutionPolicy BuildPolicy(Controls controls, string? sourceSettingsVersion) => ExecutionPolicy.Create(
        new OperatorOptions
        {
            MaxToolLoops = controls.MaxToolLoops,
            MaxHistoryMessages = controls.MaxHistoryMessages,
            MaxTokensPerConversation = controls.MaxTokensPerConversation,
        },
        new QueueOptions { MaxRetries = controls.MaxRetries, StaleLockMinutes = controls.StaleLockMinutes },
        new AiOptions
        {
            MaxTokens = controls.MaxOutputTokens,
            AttemptTimeoutSeconds = controls.Ai.AttemptTimeoutSeconds,
            TotalRequestTimeoutSeconds = controls.Ai.TotalRequestTimeoutSeconds,
            StreamFirstByteSeconds = controls.Ai.StreamFirstByteSeconds,
            StreamIdleSeconds = controls.Ai.StreamIdleSeconds,
            MaxStreamSeconds = controls.Ai.MaxStreamSeconds,
            MaxStreamBytes = controls.Ai.MaxStreamBytes,
            MaxStreamEventBytes = controls.Ai.MaxStreamEventBytes,
        },
        new ResponseOptions { Detail = controls.Detail },
        new PromptOptions { AdditionalInstructions = controls.AdditionalInstructions },
        sourceSettingsVersion, controls.Upload, controls.Document);

    private static Controls ReadLocalControls(IConfiguration local)
    {
        int Read(string key)
        {
            var control = AppliedControls.First(c => c.Key == key);
            if (TryCanonicalValue(control, control.ValueType, local[key] ?? "", out var canonical))
                return int.Parse(canonical, CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"Konfigurasi lokal {key} wajib bilangan bulat {control.Min}..{control.Max}.");
        }

        // Opsional: kosong berarti tidak ada batas dari chatbot (gateway yang menentukan); terisi tapi rusak tetap gagal keras.
        int? ReadOptional(string key) => string.IsNullOrWhiteSpace(local[key]) ? null : Read(key);

        var detail = AppliedControls.First(c => c.Key == "Response:Detail");
        var detailText = local["Response:Detail"];
        if (string.IsNullOrEmpty(detailText)) detailText = "mendalam";
        if (!TryCanonicalValue(detail, detail.ValueType, detailText, out var canonicalDetail))
            throw new InvalidOperationException("Konfigurasi lokal Response:Detail wajib ringkas, normal, atau mendalam.");

        return new Controls(
            Read("Operator:MaxToolLoops"), Read("Operator:MaxHistoryMessages"),
            Read("Operator:MaxTokensPerConversation"), Read("Queue:MaxRetries"), Read("Queue:StaleLockMinutes"),
            ReadOptional("Ai:MaxTokens"), canonicalDetail, Read("Dispatcher:MaxConcurrentOperators"),
            Read("Dispatcher:MaxConcurrentSpecialists"), Read("Dispatcher:SweepSeconds"),
            local["Dispatcher:OperatorDll"] ?? "", local["Dispatcher:SpecialistDll"] ?? "",
            local["Prompt:AdditionalInstructions"] ?? "",
            AiRuntimeLimits.FromOptions(local.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions()),
            UploadRuntimeLimits.FromOptions(local.GetSection(UploadOptions.Section).Get<UploadOptions>() ?? new UploadOptions()),
            DocumentRuntimeLimits.FromOptions(local.GetSection(DocumentOptions.Section).Get<DocumentOptions>() ?? new DocumentOptions()));
    }

    private static bool TryCanonicalValue(Control control, string valueType, string value, out string canonical)
    {
        canonical = "";
        if (valueType != control.ValueType) return false;
        if (control.Key == "Upload:AllowedExtensions")
        {
            if (value.Length > control.MaxLength) return false;
            try
            {
                canonical = string.Join(',', UploadRuntimeLimits.ParseExtensions(value));
                return true;
            }
            catch (ValidationException) { return false; }
        }
        if (control.AllowedValues is { } allowed)
        {
            var text = value.Trim().ToLowerInvariant();
            if (!allowed.Contains(text, StringComparer.Ordinal)) return false;
            canonical = text;
            return true;
        }
        if (control.Min is { } min && control.Max is { } max)
        {
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                || number < min || number > max)
                return false;
            canonical = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        // Teks bebas: Required menolak kosong (path DLL), prompt tambahan boleh kosong seperti PromptOptions.
        if (control.Required && string.IsNullOrWhiteSpace(value)) return false;
        if (control.MaxLength is { } maxLength && value.Length > maxLength) return false;
        canonical = value;
        return true;
    }

    private static Controls ApplyControl(Controls controls, string key, string value) => key switch
    {
        "Operator:MaxToolLoops" => controls with { MaxToolLoops = int.Parse(value, CultureInfo.InvariantCulture) },
        "Operator:MaxHistoryMessages" => controls with { MaxHistoryMessages = int.Parse(value, CultureInfo.InvariantCulture) },
        "Operator:MaxTokensPerConversation" => controls with { MaxTokensPerConversation = int.Parse(value, CultureInfo.InvariantCulture) },
        "Queue:MaxRetries" => controls with { MaxRetries = int.Parse(value, CultureInfo.InvariantCulture) },
        "Queue:StaleLockMinutes" => controls with { StaleLockMinutes = int.Parse(value, CultureInfo.InvariantCulture) },
        "Ai:MaxTokens" => controls with { MaxOutputTokens = int.Parse(value, CultureInfo.InvariantCulture) },
        "Response:Detail" => controls with { Detail = value },
        "Dispatcher:MaxConcurrentOperators" => controls with { MaxConcurrentOperators = int.Parse(value, CultureInfo.InvariantCulture) },
        "Dispatcher:MaxConcurrentSpecialists" => controls with { MaxConcurrentSpecialists = int.Parse(value, CultureInfo.InvariantCulture) },
        "Dispatcher:SweepSeconds" => controls with { SweepSeconds = int.Parse(value, CultureInfo.InvariantCulture) },
        "Dispatcher:OperatorDll" => controls with { OperatorDll = value },
        "Dispatcher:SpecialistDll" => controls with { SpecialistDll = value },
        "Prompt:AdditionalInstructions" => controls with { AdditionalInstructions = value },
        "Ai:AttemptTimeoutSeconds" => controls with { Ai = controls.Ai with { AttemptTimeoutSeconds = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Ai:TotalRequestTimeoutSeconds" => controls with { Ai = controls.Ai with { TotalRequestTimeoutSeconds = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Ai:StreamFirstByteSeconds" => controls with { Ai = controls.Ai with { StreamFirstByteSeconds = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Ai:StreamIdleSeconds" => controls with { Ai = controls.Ai with { StreamIdleSeconds = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Ai:MaxStreamSeconds" => controls with { Ai = controls.Ai with { MaxStreamSeconds = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Ai:MaxStreamBytes" => controls with { Ai = controls.Ai with { MaxStreamBytes = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Ai:MaxStreamEventBytes" => controls with { Ai = controls.Ai with { MaxStreamEventBytes = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Upload:MaxBytes" => controls with { Upload = controls.Upload with { MaxBytes = long.Parse(value, CultureInfo.InvariantCulture) } },
        "Upload:AllowedExtensions" => controls with { Upload = controls.Upload with { AllowedExtensions = UploadRuntimeLimits.ParseExtensions(value) } },
        "Upload:MaxAttachmentsPerMessage" => controls with { Upload = controls.Upload with { MaxAttachmentsPerMessage = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:MaxPages" => controls with { Document = controls.Document with { MaxPages = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:MaxExtractedChars" => controls with { Document = controls.Document with { MaxExtractedChars = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:MaxImagePixels" => controls with { Document = controls.Document with { MaxImagePixels = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:VisionMaxLongEdge" => controls with { Document = controls.Document with { VisionMaxLongEdge = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:VisionMinLongEdge" => controls with { Document = controls.Document with { VisionMinLongEdge = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:VisionMaxImageBytes" => controls with { Document = controls.Document with { VisionMaxImageBytes = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:VisionMaxImagesPerRequest" => controls with { Document = controls.Document with { VisionMaxImagesPerRequest = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:VisionMaxBase64CharsPerRequest" => controls with { Document = controls.Document with { VisionMaxBase64CharsPerRequest = int.Parse(value, CultureInfo.InvariantCulture) } },
        "Document:WatchdogSeconds" => controls with { Document = controls.Document with { WatchdogSeconds = int.Parse(value, CultureInfo.InvariantCulture) } },
        _ => throw new System.Diagnostics.UnreachableException(),
    };

    /// <summary>Hanya kontrol telemetri; prompt, DLL, dan key lain tidak pernah masuk effectiveValues. <c>Ai:MaxTokens</c> hanya bila diset.</summary>
    private static Dictionary<string, string> EffectiveValues(Controls controls)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Operator:MaxToolLoops"] = controls.MaxToolLoops.ToString(CultureInfo.InvariantCulture),
            ["Operator:MaxHistoryMessages"] = controls.MaxHistoryMessages.ToString(CultureInfo.InvariantCulture),
            ["Operator:MaxTokensPerConversation"] = controls.MaxTokensPerConversation.ToString(CultureInfo.InvariantCulture),
            ["Queue:MaxRetries"] = controls.MaxRetries.ToString(CultureInfo.InvariantCulture),
            ["Queue:StaleLockMinutes"] = controls.StaleLockMinutes.ToString(CultureInfo.InvariantCulture),
            ["Response:Detail"] = controls.Detail,
            ["Dispatcher:MaxConcurrentOperators"] = controls.MaxConcurrentOperators.ToString(CultureInfo.InvariantCulture),
            ["Dispatcher:MaxConcurrentSpecialists"] = controls.MaxConcurrentSpecialists.ToString(CultureInfo.InvariantCulture),
            ["Dispatcher:SweepSeconds"] = controls.SweepSeconds.ToString(CultureInfo.InvariantCulture),
            ["Ai:AttemptTimeoutSeconds"] = controls.Ai.AttemptTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["Ai:TotalRequestTimeoutSeconds"] = controls.Ai.TotalRequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["Ai:StreamFirstByteSeconds"] = controls.Ai.StreamFirstByteSeconds.ToString(CultureInfo.InvariantCulture),
            ["Ai:StreamIdleSeconds"] = controls.Ai.StreamIdleSeconds.ToString(CultureInfo.InvariantCulture),
            ["Ai:MaxStreamSeconds"] = controls.Ai.MaxStreamSeconds.ToString(CultureInfo.InvariantCulture),
            ["Ai:MaxStreamBytes"] = controls.Ai.MaxStreamBytes.ToString(CultureInfo.InvariantCulture),
            ["Ai:MaxStreamEventBytes"] = controls.Ai.MaxStreamEventBytes.ToString(CultureInfo.InvariantCulture),
            ["Upload:MaxBytes"] = controls.Upload.MaxBytes.ToString(CultureInfo.InvariantCulture),
            ["Upload:AllowedExtensions"] = string.Join(',', controls.Upload.AllowedExtensions),
            ["Upload:MaxAttachmentsPerMessage"] = controls.Upload.MaxAttachmentsPerMessage.ToString(CultureInfo.InvariantCulture),
            ["Document:MaxPages"] = controls.Document.MaxPages.ToString(CultureInfo.InvariantCulture),
            ["Document:MaxExtractedChars"] = controls.Document.MaxExtractedChars.ToString(CultureInfo.InvariantCulture),
            ["Document:MaxImagePixels"] = controls.Document.MaxImagePixels.ToString(CultureInfo.InvariantCulture),
            ["Document:VisionMaxLongEdge"] = controls.Document.VisionMaxLongEdge.ToString(CultureInfo.InvariantCulture),
            ["Document:VisionMinLongEdge"] = controls.Document.VisionMinLongEdge.ToString(CultureInfo.InvariantCulture),
            ["Document:VisionMaxImageBytes"] = controls.Document.VisionMaxImageBytes.ToString(CultureInfo.InvariantCulture),
            ["Document:VisionMaxImagesPerRequest"] = controls.Document.VisionMaxImagesPerRequest.ToString(CultureInfo.InvariantCulture),
            ["Document:VisionMaxBase64CharsPerRequest"] = controls.Document.VisionMaxBase64CharsPerRequest.ToString(CultureInfo.InvariantCulture),
            ["Document:WatchdogSeconds"] = controls.Document.WatchdogSeconds.ToString(CultureInfo.InvariantCulture),
        };
        // Tidak diset = tidak dilaporkan (bukan nol palsu): gateway menampilkan batas keluaran dari policy/model-nya sendiri.
        if (controls.MaxOutputTokens is { } maxOutput)
            values["Ai:MaxTokens"] = maxOutput.ToString(CultureInfo.InvariantCulture);
        return values;
    }

    private static List<string> Capped(List<string> keys) =>
        keys.Count <= MaxReportedKeys ? keys : keys.GetRange(0, MaxReportedKeys);

    private static string RejectionReason(List<string> rejected)
    {
        var extra = rejected.Count > 1 ? $" (+{rejected.Count - 1} key lain)" : "";
        return Bound($"Pengaturan dari gateway ditolak: {rejected[0]}{extra} tidak valid.");
    }

    private static string Bound(string? message)
    {
        var text = (message ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
    }
}

public static class GatewaySettingsExtensions
{
    /// <summary>Hanya API yang polling; worker tetap satu proses per tiket dan tidak membutuhkan Settings.</summary>
    public static IServiceCollection AddGatewaySettings(this IServiceCollection services, ConfigurationManager configuration)
    {
        // Baseline lokal dibaca dari konfigurasi lengkap sebelum sumber gateway ditambahkan; nilai ini yang
        // dipakai CurrentPolicy sampai ada snapshot gateway yang lolos, dan saat gateway menghapus nilai.
        var provider = new GatewaySettingsProvider(configuration);
        ((IConfigurationBuilder)configuration).Add(provider);
        services.AddSingleton(provider);
        services.AddOptions<SettingsOptions>().Bind(configuration.GetSection(SettingsOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AiOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Ai:ApiKey wajib di API untuk pengaturan gateway (key gw_..., lewat user-secrets).")
            .ValidateOnStart();
        services.AddHttpClient("gateway-settings", (sp, http) =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            http.BaseAddress = new Uri(ai.BaseUrl);
            http.DefaultRequestHeaders.Authorization = new("Bearer", ai.ApiKey);
            http.Timeout = TimeSpan.FromSeconds(10);
            http.MaxResponseContentBufferSize = 4 * 1024 * 1024; // 200 nilai × 2000 karakter, termasuk escape Unicode JSON.
        });
        services.AddHostedService<GatewaySettingsPoller>();
        return services;
    }
}

/// <summary>
/// Poller API: segarkan pengaturan lalu laporkan status secara berkala lewat HttpClient ber-key sama. Kegagalan
/// apa pun pada laporan hanya dicatat; status terakhir tetap berlaku dan laporan yang gagal dibaca gateway sebagai
/// stale, bukan sebagai nol.
/// </summary>
public sealed class GatewaySettingsPoller(
    GatewaySettingsProvider provider,
    IHttpClientFactory clients,
    IOptions<SettingsOptions> settings,
    QueueRepository queue,
    TimeProvider clock,
    ILogger<GatewaySettingsPoller> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Satu putaran: segarkan pengaturan lalu POST laporan; aman dipanggil berulang tanpa memuat state baru.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        using var http = clients.CreateClient("gateway-settings");
        try
        {
            if (await provider.RefreshAsync(http, ct))
                logger.LogInformation("Pengaturan dari gateway diperbarui");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Pengaturan gateway tidak diperbarui ({Reason}); memakai nilai terakhir atau konfigurasi lokal", ex.Message);
        }

        QueueRuntimeSnapshot runtime;
        try
        {
            runtime = await queue.ReadRuntimeSnapshotAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tanpa hitungan asli laporan dilewati: status terakhir menjadi stale, bukan nol palsu.
            logger.LogWarning("Hitungan antrean gagal, laporan status gateway dilewati ({Reason})", ex.Message);
            return;
        }

        try
        {
            // Laporan yang tidak bisa disusun valid (mis. kebijakan aktif terlalu banyak) dilewati utuh:
            // status terakhir di gateway menjadi stale, bukan laporan separuh atau nol palsu.
            var report = provider.CreateReport(runtime, settings.Value.RefreshSeconds, clock.GetUtcNow());
            using var response = await http.PostAsJsonAsync("/client/api/app-status", report, ReportJson, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Laporan status ke gateway gagal ({Reason})", ex.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.Value.RefreshSeconds));
        try
        {
            do
            {
                await PollOnceAsync(ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
