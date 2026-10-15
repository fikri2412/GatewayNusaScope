using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OperatorAi.Shared.Ai;

namespace OperatorAi.Shared.Options;

/// <summary>Kebijakan immutable satu workflow; perubahan settings hanya berlaku pada pesan baru.</summary>
public sealed record ExecutionPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [JsonRequired, Range(1, 20)] public int MaxToolLoops { get; init; }
    [JsonRequired, Range(1, 500)] public int MaxHistoryMessages { get; init; }
    [JsonRequired, Range(1000, int.MaxValue)] public int MaxTokensPerConversation { get; init; }
    [JsonRequired, Range(1, 20)] public int MaxRetries { get; init; }
    [JsonRequired, Range(1, 1440)] public int StaleLockMinutes { get; init; }
    /// <summary>Null = tidak ada batas dari chatbot; <c>max_tokens</c> tidak dikirim dan gateway yang menentukan.</summary>
    [JsonRequired, Range(1, 64000)] public int? MaxOutputTokens { get; init; }
    [JsonRequired, Required, AllowedValues("ringkas", "normal", "mendalam")] public string Detail { get; init; } = "";
    [JsonRequired, MaxLength(2000)] public string AdditionalInstructions { get; init; } = "";
    [JsonRequired, Required] public AiRuntimeLimits Ai { get; init; } = AiRuntimeLimits.Default;
    [JsonRequired, Required] public UploadRuntimeLimits Upload { get; init; } = UploadRuntimeLimits.Default;
    [JsonRequired, Required] public DocumentRuntimeLimits Document { get; init; } = DocumentRuntimeLimits.Default;
    [JsonRequired, MaxLength(128)] public string? SourceSettingsVersion { get; init; }
    [JsonRequired, Required] public string PromptHash { get; init; } = "";
    [JsonRequired] public string PolicyHash { get; init; } = "";

    public static ExecutionPolicy Create(OperatorOptions op, QueueOptions queue, AiOptions ai,
        ResponseOptions response, PromptOptions prompt, string? sourceSettingsVersion = null,
        UploadRuntimeLimits? upload = null, DocumentRuntimeLimits? document = null)
    {
        var policy = new ExecutionPolicy
        {
            MaxToolLoops = op.MaxToolLoops,
            MaxHistoryMessages = op.MaxHistoryMessages,
            MaxTokensPerConversation = op.MaxTokensPerConversation,
            MaxRetries = queue.MaxRetries,
            StaleLockMinutes = queue.StaleLockMinutes,
            MaxOutputTokens = ai.MaxTokens,
            Detail = response.Detail,
            AdditionalInstructions = prompt.AdditionalInstructions,
            SourceSettingsVersion = sourceSettingsVersion,
            PromptHash = PromptPolicy.Hash,
            Ai = AiRuntimeLimits.FromOptions(ai),
            Upload = upload ?? UploadRuntimeLimits.Default,
            Document = document ?? DocumentRuntimeLimits.Default,
        };
        policy.ValidateValues();
        return policy with { PolicyHash = policy.ComputeHash() };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ExecutionPolicy FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16000)
            throw new FormatException("Snapshot kebijakan pekerjaan hilang atau terlalu besar; lakukan cutover antrean.");
        try
        {
            var policy = JsonSerializer.Deserialize<ExecutionPolicy>(json, Json)
                ?? throw new FormatException("Snapshot kebijakan pekerjaan bukan objek.");
            policy.ValidateValues();
            if (policy.PromptHash != PromptPolicy.Hash)
                throw new FormatException("Versi prompt pekerjaan berbeda; drain antrean sebelum upgrade template.");
            if (!string.Equals(policy.PolicyHash, policy.ComputeHash(), StringComparison.Ordinal))
                throw new FormatException("Hash snapshot kebijakan pekerjaan tidak cocok.");
            return policy;
        }
        catch (Exception ex) when (ex is JsonException or ValidationException)
        {
            throw new FormatException("Snapshot kebijakan pekerjaan tidak valid.", ex);
        }
    }

    private void ValidateValues()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        if (AdditionalInstructions is null)
            throw new ValidationException("Instruksi tambahan tidak boleh null.");
        Ai.Validate();
        Upload.Validate();
        Document.Validate();
    }

    private string ComputeHash()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("maxToolLoops", MaxToolLoops);
            writer.WriteNumber("maxHistoryMessages", MaxHistoryMessages);
            writer.WriteNumber("maxTokensPerConversation", MaxTokensPerConversation);
            writer.WriteNumber("maxRetries", MaxRetries);
            writer.WriteNumber("staleLockMinutes", StaleLockMinutes);
            if (MaxOutputTokens is { } maxOutput) writer.WriteNumber("maxOutputTokens", maxOutput);
            else writer.WriteNull("maxOutputTokens");
            writer.WriteString("detail", Detail);
            writer.WriteString("additionalInstructions", AdditionalInstructions);
            writer.WriteString("sourceSettingsVersion", SourceSettingsVersion);
            writer.WriteString("promptHash", PromptHash);
            writer.WritePropertyName("ai");
            JsonSerializer.Serialize(writer, Ai, Json);
            writer.WritePropertyName("upload");
            JsonSerializer.Serialize(writer, Upload, Json);
            writer.WritePropertyName("document");
            JsonSerializer.Serialize(writer, Document, Json);
            writer.WriteEndObject();
        }
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer.WrittenSpan, hash);
        return Convert.ToHexStringLower(hash);
    }
}
