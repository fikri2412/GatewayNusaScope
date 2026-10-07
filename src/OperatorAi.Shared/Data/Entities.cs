using OperatorAi.Shared.Queue;

namespace OperatorAi.Shared.Data;

public class Conversation
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Message
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string Role { get; set; }
    public string? Content { get; set; }
    public string? AiContentJson { get; set; }
    public bool IsVisible { get; set; } = true;
    public string? AttachmentIdsJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Attachment
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string FileName { get; set; }
    public required string StoragePath { get; set; }
    public required string Kind { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class QueueItem
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string Type { get; set; }
    public required string PayloadJson { get; set; }
    public string Status { get; set; } = Statuses.Pending;
    public DateTime? LockedAt { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class Job
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string JobType { get; set; }
    public required string InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string Status { get; set; } = Statuses.Pending;
    public long? DependsOnJobId { get; set; }
    public bool NotifyOperator { get; set; }
    public DateTime? LockedAt { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class AiCall
{
    public long Id { get; set; }
    public Guid? ConversationId { get; set; }
    public required string Source { get; set; }
    public required string Model { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int DurationMs { get; set; }
    public string? StopReason { get; set; }
    public string? ToolsCalled { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
}
