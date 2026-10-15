using Microsoft.AspNetCore.SignalR;
using OperatorAi.Api.Auth;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;

namespace OperatorAi.Api.Hubs;

public sealed class ChatHub(AppDbContext db) : Hub
{
    public async Task JoinConversation(Guid conversationId)
    {
        var userId = Context.User!.UserId();
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId && c.UserId == userId))
            throw new HubException("Percakapan tidak ditemukan.");
        await Groups.AddToGroupAsync(Context.ConnectionId, ChatNotifier.Group(conversationId));
    }

    public Task LeaveConversation(Guid conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatNotifier.Group(conversationId));
}

/// <summary>Kirim event ke group percakapan. Data sudah tersimpan saat ini dipanggil, jadi gagal kirim hanya di-log.</summary>
public sealed class ChatNotifier(IHubContext<ChatHub> hub, IServiceScopeFactory scopes, ILogger<ChatNotifier> logger)
{
    public static string Group(Guid conversationId) => $"conv:{conversationId}";

    /// <summary>Jalur yang sudah memegang DTO-nya (POST /messages).</summary>
    public Task MessageCreatedAsync(Guid conversationId, MessageDto dto) =>
        SendAsync(conversationId, NotifyTypes.MessageCreated, dto);

    /// <summary>
    /// Batch draft dari worker (<c>POST /internal/stream</c>). Tidak disimpan dan tidak membangunkan dispatcher:
    /// kegagalan kirim berarti batch itu hilang, sedangkan balasan canonical tetap di-commit lewat notify.
    /// </summary>
    public Task StreamEventAsync(Guid conversationId, StreamEvent evt, CancellationToken ct = default) =>
        SendAsync(conversationId, NotifyTypes.StreamEvent, evt, ct);

    /// <summary>Muat pesan dari database lalu dorong (notify worker, permintaan maaf dispatcher). False bila tidak ada.</summary>
    public async Task<bool> PublishMessageAsync(Guid conversationId, long messageId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = await db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == conversationId, ct);
        if (message is null) return false;
        if (message.IsVisible) await MessageCreatedAsync(conversationId, (await db.ToDtosAsync([message], ct))[0]);
        // Snapshot tiket aktif setelah pesan canonical: UI tahu tiket masih berjalan (putaran tool) atau sudah selesai,
        // termasuk saat balasan assistant terakhir disembunyikan atau hanya bagian dari putaran tool.
        await SendActivityAsync(db, conversationId, ct);
        return true;
    }

    /// <summary>
    /// Snapshot tiket aktif percakapan, atas permintaan dispatcher setelah recovery mengubah status tiket
    /// (permintaan maaf/release tidak lewat notify pesan).
    /// </summary>
    public async Task PublishActivityAsync(Guid conversationId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SendActivityAsync(db, conversationId, ct);
    }

    /// <summary>
    /// Snapshot atas permintaan worker (tiket terminal tanpa balasan canonical, mis. gagal tanpa permintaan
    /// maaf atau release oleh host). Id harus tiket percakapan itu, kalau tidak false (404 di endpoint internal).
    /// </summary>
    public async Task<bool> PublishActivityAsync(Guid conversationId, long queueItemId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.QueueItems.AsNoTracking().AnyAsync(q => q.Id == queueItemId && q.ConversationId == conversationId, ct))
            return false;
        await SendActivityAsync(db, conversationId, ct);
        return true;
    }

    private async Task SendActivityAsync(AppDbContext db, Guid conversationId, CancellationToken ct) =>
        await SendAsync(conversationId, NotifyTypes.ActivityChanged, await db.ActiveTicketsAsync(conversationId, ct));

    public async Task<bool> PublishJobAsync(Guid conversationId, long jobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.ConversationId == conversationId, ct);
        if (job is null) return false;
        await SendAsync(conversationId, NotifyTypes.JobUpdated, job.ToDto());
        return true;
    }

    private async Task SendAsync(Guid conversationId, string method, object payload, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.Group(Group(conversationId)).SendAsync(method, payload, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kirim {Event} ke percakapan {ConversationId} gagal", method, conversationId);
        }
    }
}
