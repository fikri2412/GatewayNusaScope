namespace OperatorAi.Shared.Contracts;

public sealed record StatusCounts(long Pending, long Processing, long Done, long Failed);

public sealed record ActivePolicyCount(string PolicyHash, long QueueItems, long Jobs);

public sealed record QueueRuntimeSnapshot(StatusCounts Queue, StatusCounts Jobs,
    IReadOnlyList<ActivePolicyCount> ActivePolicies);

public sealed record AppSettingMetadata(string Key, string ValueType, int? Min = null, int? Max = null,
    int? MaxLength = null, IReadOnlyList<string>? AllowedValues = null, string? Description = null, bool Redacted = false);

public sealed record AppStatusReport(string Namespace, string? SourceSettingsVersion, string PolicyHash, string PromptHash,
    IReadOnlyDictionary<string, string> EffectiveValues, IReadOnlyList<AppSettingMetadata> SupportedKeys,
    string SyncStatus, DateTimeOffset? LastConfigSuccessAt, IReadOnlyList<string> RejectedKeys,
    IReadOnlyList<string> IgnoredKeys, string? Reason, StatusCounts Queue, StatusCounts Jobs,
    IReadOnlyList<ActivePolicyCount> ActivePolicies, int ReportIntervalSeconds, DateTimeOffset ObservedAt);
