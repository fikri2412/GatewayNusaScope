using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Api;

public static class Mapping
{
    // SQL Server mengembalikan DateTime tanpa Kind; tanpa ini JSON-nya tidak ber-"Z" dan browser menganggapnya waktu lokal.
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public static ConversationDto ToDto(this Conversation c) =>
        new(c.Id, c.Title, Utc(c.CreatedAt), c.TokensUsed, c.UsageIncomplete, c.ModelAlias);

    public static JobDto ToDto(this Job j) => new(j.Id, j.JobType, j.Status, j.Error, Utc(j.CreatedAt));

    public static AttachmentDto ToDto(this Attachment a) =>
        new(a.Id, a.FileName, a.Kind, a.SizeBytes, $"/api/attachments/{a.Id}/download", a.ContentType);

    public static SavedAnswerDto ToDto(this SavedAnswer s) =>
        new(s.Id, s.Content, s.ModelAlias, s.SessionTitle, s.ConversationId, s.MessageId, Utc(s.CreatedAt),
            s.ConversationId is not null);

    public static ArtifactSummaryDto ToSummary(this Artifact a) =>
        new(a.Id, a.Kind, a.Title, a.Language, a.CurrentVersion, a.SourceMessageId, Utc(a.CreatedAt), Utc(a.UpdatedAt));

    public static ArtifactDto ToDto(this Artifact a, string content) =>
        new(a.Id, a.Kind, a.Title, a.Language, a.CurrentVersion, a.SourceMessageId, Utc(a.CreatedAt), Utc(a.UpdatedAt), content);

    public static async Task<List<MessageDto>> ToDtosAsync(this AppDbContext db, IReadOnlyList<Message> messages,
        CancellationToken ct)
    {
        var idsByMessage = messages.ToDictionary(m => m.Id, m => ParseIds(m.AttachmentIdsJson));
        var allIds = idsByMessage.Values.SelectMany(x => x).Distinct().ToList();
        var attachments = allIds.Count == 0
            ? []
            : await db.Attachments.AsNoTracking().Where(a => allIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        // Pesan sumber jawaban hanya diketahui lewat tiket yang menulis barisnya (hubungan tanpa FK, §4.2).
        var queueIds = messages.Select(m => m.QueueItemId).OfType<long>().Distinct().ToList();
        var sources = queueIds.Count == 0
            ? []
            : await db.QueueItems.AsNoTracking().Where(q => queueIds.Contains(q.Id))
                .ToDictionaryAsync(q => q.Id, q => q.SourceMessageId, ct);

        return messages.Select(m => new MessageDto(m.Id, m.Role, m.Content, Utc(m.CreatedAt),
            idsByMessage[m.Id].Where(attachments.ContainsKey).Select(id => attachments[id].ToDto()).ToList(), m.FinishReason,
            m.ModelAlias, m.StreamId, m.QueueItemId,
            m.QueueItemId is { } itemId && sources.TryGetValue(itemId, out var source) ? source : null,
            m.IsPrimary, m.TokensUsed, m.QueueWaitMs, m.FirstTokenMs, m.TotalMs))
            .ToList();
    }

    /// <summary>
    /// Tiket yang masih dikerjakan (Pending/Processing) satu percakapan; dasar <c>GET /activity</c> dan
    /// snapshot <c>activityChanged</c>. Urut id supaya UI bisa membedakan tiket lama dan baru.
    /// </summary>
    public static async Task<List<ActivityDto>> ActiveTicketsAsync(this AppDbContext db, Guid conversationId, CancellationToken ct)
    {
        var rows = await db.QueueItems.AsNoTracking()
            .Where(q => q.ConversationId == conversationId && (q.Status == Statuses.Pending || q.Status == Statuses.Processing))
            .OrderBy(q => q.Id)
            .Select(q => new { q.Id, q.Status, q.SourceMessageId, q.Type, q.IsPrimary, q.PayloadJson })
            .ToListAsync(ct);
        return rows.Select(q => new ActivityDto(q.Id, q.Status, q.SourceMessageId, PayloadModel(q.PayloadJson), q.Type,
            !q.IsPrimary)).ToList();
    }

    /// <summary>Alias model dari snapshot payload tiket; null untuk payload lama/tanpa model, bukan galat ke pemanggil.</summary>
    public static string? PayloadModel(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            return ModelSnapshot.Read(payloadJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    internal static Guid[] ParseIds(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Guid[]>(json) ?? [];
}
