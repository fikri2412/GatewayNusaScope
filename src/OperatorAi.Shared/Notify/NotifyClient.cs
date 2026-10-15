using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Shared.Notify;

/// <summary>
/// Worker → <c>POST /internal/notify</c>. Kegagalan notify tidak boleh menggagalkan tiket (DESIGN bagian 7):
/// cukup Warning, karena client memuat ulang riwayat saat reconnect.
/// </summary>
public sealed class NotifyClient(HttpClient http, ILogger<NotifyClient> logger)
{
    public async Task NotifyAsync(string type, Guid conversationId, long id, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.PostAsJsonAsync("internal/notify", new NotifyRequest(type, conversationId, id), ct);
            if (!res.IsSuccessStatusCode)
                logger.LogWarning("Notify {Type} ditolak API: {Status}", type, (int)res.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Notify {Type} gagal (API tidak terjangkau?)", type);
        }
    }

    /// <summary>Draft best-effort: notify gagal tidak boleh membuang hasil canonical; run lama harus berhenti.</summary>
    public async Task<bool> StreamAsync(StreamEvent batch, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var res = await http.PostAsJsonAsync("internal/stream", batch, deadline.Token);
            if (res.StatusCode == System.Net.HttpStatusCode.Conflict) throw new LeaseLostException();
            if (res.IsSuccessStatusCode) return true;
            logger.LogWarning("Stream notify ditolak API: {Status}; tunggu pesan canonical", (int)res.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not LeaseLostException)
        {
            logger.LogWarning(ex, "Stream notify gagal; tunggu pesan canonical");
        }
        return false;
    }

    /// <summary>Beritahu API tentang pesan yang baru ditulis repository (balasan, permintaan maaf).</summary>
    public async Task NotifyMessagesAsync(IEnumerable<MessageRef> messages, CancellationToken ct = default)
    {
        foreach (var m in messages) await NotifyAsync(NotifyTypes.MessageCreated, m.ConversationId, m.MessageId, ct);
    }

    /// <summary>Beritahu API bahwa job berubah status (browser memperbarui <c>JobStatusBar</c>).</summary>
    public async Task NotifyJobsAsync(IEnumerable<JobRef> jobs, CancellationToken ct = default)
    {
        foreach (var j in jobs) await NotifyAsync(NotifyTypes.JobUpdated, j.ConversationId, j.JobId, ct);
    }
}

public static class NotifyServiceCollectionExtensions
{
    public static IServiceCollection AddNotifyClient(this IServiceCollection services)
    {
        services.AddHttpClient<NotifyClient>((sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ApiOptions>>().Value.BaseUrl.TrimEnd('/') + "/");
            c.DefaultRequestHeaders.Add("X-Internal-Key", sp.GetRequiredService<IOptions<InternalOptions>>().Value.Key);
            c.Timeout = TimeSpan.FromSeconds(5);
        });
        return services;
    }
}
