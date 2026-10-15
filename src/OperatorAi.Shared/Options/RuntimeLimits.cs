using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OperatorAi.Shared.Options;

/// <summary>Ceilings server: gateway dapat memperketat policy, bukan melewati kemampuan/anggaran aman aplikasi.</summary>
public static class RuntimeSafetyLimits
{
    public const int AiAttemptSeconds = 600, AiTotalSeconds = 3600;
    public const int StreamFirstByteSeconds = 300, StreamIdleSeconds = 3600, StreamSeconds = 86400;
    public const int StreamBytes = 8 * 1024 * 1024, StreamEventBytes = 256 * 1024;
    public const int UploadBytes = 10 * 1024 * 1024, AttachmentsPerMessage = 6;
    public const int PdfPages = 100, ExtractedChars = 100_000, ImagePixels = 16_000_000;
    public const int VisionLongEdge = 1280, VisionImageBytes = 400_000;
    public const int VisionBatchImages = 4, VisionBase64Chars = 2_000_000, DocumentWatchdogSeconds = 60;
    public static ImmutableArray<string> SupportedUploadExtensions { get; } = [".csv", ".jpeg", ".jpg", ".pdf", ".png", ".webp", ".xlsx"];
}

public sealed record AiRuntimeLimits
{
    public static AiRuntimeLimits Default { get; } = new();
    [JsonRequired, Range(1, RuntimeSafetyLimits.AiAttemptSeconds)] public int AttemptTimeoutSeconds { get; init; } = 60;
    [JsonRequired, Range(1, RuntimeSafetyLimits.AiTotalSeconds)] public int TotalRequestTimeoutSeconds { get; init; } = 120;
    [JsonRequired, Range(1, RuntimeSafetyLimits.StreamFirstByteSeconds)] public int StreamFirstByteSeconds { get; init; } = 30;
    [JsonRequired, Range(1, RuntimeSafetyLimits.StreamIdleSeconds)] public int StreamIdleSeconds { get; init; } = 60;
    [JsonRequired, Range(1, RuntimeSafetyLimits.StreamSeconds)] public int MaxStreamSeconds { get; init; } = 900;
    [JsonRequired, Range(1, RuntimeSafetyLimits.StreamBytes)] public int MaxStreamBytes { get; init; } = RuntimeSafetyLimits.StreamBytes;
    [JsonRequired, Range(1, RuntimeSafetyLimits.StreamEventBytes)] public int MaxStreamEventBytes { get; init; } = RuntimeSafetyLimits.StreamEventBytes;

    public void Validate()
    {
        Validator.ValidateObject(this, new ValidationContext(this), true);
        if (TotalRequestTimeoutSeconds < AttemptTimeoutSeconds)
            throw new ValidationException("Ai:TotalRequestTimeoutSeconds harus >= Ai:AttemptTimeoutSeconds.");
        if (StreamFirstByteSeconds > AttemptTimeoutSeconds)
            throw new ValidationException("Ai:StreamFirstByteSeconds harus <= Ai:AttemptTimeoutSeconds.");
        if (MaxStreamEventBytes > MaxStreamBytes)
            throw new ValidationException("Ai:MaxStreamEventBytes harus <= Ai:MaxStreamBytes.");
    }

    public static AiRuntimeLimits FromOptions(AiOptions options)
    {
        var value = new AiRuntimeLimits
        {
            AttemptTimeoutSeconds = options.AttemptTimeoutSeconds,
            TotalRequestTimeoutSeconds = options.TotalRequestTimeoutSeconds,
            StreamFirstByteSeconds = options.StreamFirstByteSeconds,
            StreamIdleSeconds = options.StreamIdleSeconds,
            MaxStreamSeconds = options.MaxStreamSeconds,
            MaxStreamBytes = options.MaxStreamBytes,
            MaxStreamEventBytes = options.MaxStreamEventBytes,
        };
        value.Validate();
        return value;
    }
}

public sealed record UploadRuntimeLimits
{
    public static UploadRuntimeLimits Default { get; } = new();
    [JsonRequired, Range(1, RuntimeSafetyLimits.UploadBytes)] public long MaxBytes { get; init; } = RuntimeSafetyLimits.UploadBytes;
    [JsonRequired] public ImmutableArray<string> AllowedExtensions { get; init; } = RuntimeSafetyLimits.SupportedUploadExtensions;
    [JsonRequired, Range(1, RuntimeSafetyLimits.AttachmentsPerMessage)] public int MaxAttachmentsPerMessage { get; init; } = RuntimeSafetyLimits.AttachmentsPerMessage;

    public void Validate()
    {
        Validator.ValidateObject(this, new ValidationContext(this), true);
        if (AllowedExtensions.IsDefaultOrEmpty || AllowedExtensions.Length > RuntimeSafetyLimits.SupportedUploadExtensions.Length)
            throw new ValidationException("Upload:AllowedExtensions harus memuat format yang didukung aplikasi.");
        for (var i = 0; i < AllowedExtensions.Length; i++)
        {
            var extension = AllowedExtensions[i];
            if (!RuntimeSafetyLimits.SupportedUploadExtensions.Contains(extension, StringComparer.Ordinal))
                throw new ValidationException("Upload:AllowedExtensions memuat format yang tidak didukung aplikasi.");
            for (var j = 0; j < i; j++)
                if (AllowedExtensions[j] == extension)
                    throw new ValidationException("Upload:AllowedExtensions tidak boleh memuat format duplikat.");
        }
    }

    public static ImmutableArray<string> ParseExtensions(string value)
    {
        var result = value.Split(',', StringSplitOptions.TrimEntries).Select(e => e.ToLowerInvariant())
            .Order(StringComparer.Ordinal).ToImmutableArray();
        (Default with { AllowedExtensions = result }).Validate();
        return result;
    }

    public static UploadRuntimeLimits FromOptions(UploadOptions options)
    {
        var result = new UploadRuntimeLimits
        {
            MaxBytes = options.MaxBytes,
            AllowedExtensions = options.AllowedExtensions.Length == 0 ? Default.AllowedExtensions
                : options.AllowedExtensions.Select(e => e.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal).ToImmutableArray(),
            MaxAttachmentsPerMessage = options.MaxAttachmentsPerMessage,
        };
        result.Validate();
        return result;
    }
}

public sealed record DocumentRuntimeLimits
{
    public static DocumentRuntimeLimits Default { get; } = new();
    [JsonRequired, Range(1, RuntimeSafetyLimits.PdfPages)] public int MaxPages { get; init; } = RuntimeSafetyLimits.PdfPages;
    [JsonRequired, Range(1, RuntimeSafetyLimits.ExtractedChars)] public int MaxExtractedChars { get; init; } = RuntimeSafetyLimits.ExtractedChars;
    [JsonRequired, Range(1, RuntimeSafetyLimits.ImagePixels)] public int MaxImagePixels { get; init; } = RuntimeSafetyLimits.ImagePixels;
    [JsonRequired, Range(64, RuntimeSafetyLimits.VisionLongEdge)] public int VisionMaxLongEdge { get; init; } = RuntimeSafetyLimits.VisionLongEdge;
    [JsonRequired, Range(64, RuntimeSafetyLimits.VisionLongEdge)] public int VisionMinLongEdge { get; init; } = 640;
    [JsonRequired, Range(1024, RuntimeSafetyLimits.VisionImageBytes)] public int VisionMaxImageBytes { get; init; } = RuntimeSafetyLimits.VisionImageBytes;
    [JsonRequired, Range(1, RuntimeSafetyLimits.VisionBatchImages)] public int VisionMaxImagesPerRequest { get; init; } = RuntimeSafetyLimits.VisionBatchImages;
    [JsonRequired, Range(1368, RuntimeSafetyLimits.VisionBase64Chars)] public int VisionMaxBase64CharsPerRequest { get; init; } = RuntimeSafetyLimits.VisionBase64Chars;
    [JsonRequired, Range(1, RuntimeSafetyLimits.DocumentWatchdogSeconds)] public int WatchdogSeconds { get; init; } = RuntimeSafetyLimits.DocumentWatchdogSeconds;

    public void Validate()
    {
        Validator.ValidateObject(this, new ValidationContext(this), true);
        if (VisionMinLongEdge > VisionMaxLongEdge)
            throw new ValidationException("Document:VisionMinLongEdge harus <= Document:VisionMaxLongEdge.");
        if (4L * ((VisionMaxImageBytes + 2L) / 3) > VisionMaxBase64CharsPerRequest)
            throw new ValidationException("Document:VisionMaxBase64CharsPerRequest harus cukup untuk satu JPEG maksimum.");
    }

    public static DocumentRuntimeLimits FromOptions(DocumentOptions options)
    {
        var result = new DocumentRuntimeLimits
        {
            MaxPages = options.MaxPages, MaxExtractedChars = options.MaxExtractedChars,
            MaxImagePixels = options.MaxImagePixels,
            VisionMaxLongEdge = options.VisionMaxLongEdge, VisionMinLongEdge = options.VisionMinLongEdge,
            VisionMaxImageBytes = options.VisionMaxImageBytes, VisionMaxImagesPerRequest = options.VisionMaxImagesPerRequest,
            VisionMaxBase64CharsPerRequest = options.VisionMaxBase64CharsPerRequest,
            WatchdogSeconds = options.WatchdogSeconds,
        };
        result.Validate();
        return result;
    }
}
