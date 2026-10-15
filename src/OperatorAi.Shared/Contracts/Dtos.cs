namespace OperatorAi.Shared.Contracts;

/// <summary><c>TokensUsed</c> hanya lower bound bila <c>UsageIncomplete</c> (ada panggilan AI tanpa usage); UI tidak boleh menjanjikan angka pasti.</summary>
public sealed record ConversationDto(Guid Id, string? Title, DateTime CreatedAt, long TokensUsed, bool UsageIncomplete,
    string? ModelAlias);

public sealed record AttachmentDto(Guid Id, string FileName, string Kind, long SizeBytes, string DownloadUrl, string ContentType);

/// <summary><c>FinishReason</c> (mis. <c>length</c>) apa adanya dari penyedia AI; tidak ada isi reasoning di sini.
/// <c>StreamId</c>/<c>QueueItemId</c> mengaitkan balasan canonical dengan draft dan tiketnya (null untuk jalur non-stream).
/// <c>SourceMessageId</c> = pesan sumber jawaban (dari tiketnya; null untuk pesan user dan baris lama).</summary>
public sealed record MessageDto(long Id, string Role, string? Content, DateTime CreatedAt,
    IReadOnlyList<AttachmentDto> Attachments, string? FinishReason, string? ModelAlias,
    Guid? StreamId = null, long? QueueItemId = null, long? SourceMessageId = null, bool IsPrimary = true,
    int? TokensUsed = null, int? QueueWaitMs = null, int? FirstTokenMs = null, int? TotalMs = null);

public sealed record SendMessageResponse(MessageDto Message, long QueueItemId);

/// <summary>Jawaban ulang diterima/di-replay: tiket antrean yang mewakilinya dan pesan sumber yang dijawab ulang.</summary>
public sealed record TurnEnqueued(long QueueItemId, long SourceMessageId, string ModelAlias);

/// <summary>Satu cabang compare yang diterima/di-replay (id tiket + model yang dipilih untuk cabang itu).</summary>
public sealed record CompareBranch(long QueueItemId, string ModelAlias);

/// <summary>Terima/replay <c>POST /compare</c>: satu tiket per model, semuanya menjawab pesan sumber yang sama.</summary>
public sealed record CompareEnqueued(long SourceMessageId, IReadOnlyList<CompareBranch> Branches);

/// <summary>Metadata aman dari gateway; readiness adalah konfigurasi route, bukan health upstream.</summary>
public sealed record ModelInfoDto(string Id, bool? SupportsTools, bool? SupportsStreaming, bool Ready,
    int? MaxOutputTokens, string? UnavailableReason, bool? SupportsVision = null);

public sealed record JobDto(long Id, string JobType, string Status, string? Error, DateTime CreatedAt);

/// <summary>Tiket yang masih dikerjakan di satu percakapan (<c>GET /api/conversations/{id}/activity</c> dan snapshot SignalR).
/// <c>IsAlternative</c> = tiket jawaban ulang/bandingkan (bukan kiriman user biasa); <c>ModelAlias</c> dari snapshot payload.</summary>
public sealed record ActivityDto(long QueueItemId, string Status, long? SourceMessageId, string? ModelAlias,
    string Type, bool IsAlternative);

public static class NotifyTypes
{
    public const string MessageCreated = "messageCreated", JobUpdated = "jobUpdated";
    public const string StreamEvent = "streamEvent", ActivityChanged = "activityChanged";
}

/// <summary>Worker → API. <c>Id</c> = id pesan untuk <c>messageCreated</c>, id job untuk <c>jobUpdated</c>.</summary>
public sealed record NotifyRequest(string Type, Guid ConversationId, long Id);

/// <summary>
/// Batch draft streaming worker → API (<c>POST /internal/stream</c>), diteruskan apa adanya lewat SignalR.
/// <c>Sequence</c>/<c>Offset</c> menjaga chunk duplikat/gap; <c>Text</c> hanya berisi tambahan, bukan seluruh buffer.
/// </summary>
public sealed record StreamEvent(Guid ConversationId, long QueueItemId, string RunId, Guid StreamId, int Round,
    string Kind, int Sequence, int Offset, string? Text);

public static class StreamEventKinds
{
    public const string Started = "started", Delta = "delta", Aborted = "aborted";
}
