using Microsoft.EntityFrameworkCore;

namespace OperatorAi.Shared.Data;

/// <summary>
/// Pemakaian token per percakapan (<c>conversations.tokens_used</c>). Gateway mencatat per key, bukan per percakapan,
/// jadi chatbot menjumlahkan sendiri <c>usage</c> tiap respons AI (operator dan job Analyze).
/// </summary>
public static class ConversationUsage
{
    public static Task<long> TokensUsedAsync(this AppDbContext db, Guid conversationId, CancellationToken ct) =>
        db.Conversations.AsNoTracking().Where(c => c.Id == conversationId).Select(c => c.TokensUsed).SingleAsync(ct);

    /// <summary>Penambahan atomik di server, aman dari beberapa proses worker yang menulis bersamaan.</summary>
    public static Task AddTokensAsync(this AppDbContext db, Guid conversationId, long tokens, bool usageAvailable,
        CancellationToken ct) =>
        tokens <= 0 && usageAvailable
            ? Task.CompletedTask
            : db.Conversations.Where(c => c.Id == conversationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.TokensUsed, c => c.TokensUsed + Math.Max(0, tokens))
                    .SetProperty(c => c.UsageIncomplete, c => c.UsageIncomplete || !usageAvailable), ct);
}
