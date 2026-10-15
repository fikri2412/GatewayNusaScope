using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Notify;

namespace OperatorAi.Tests;

/// <summary>
/// Perkakas bersama test worker: AI berskenario, penyadap notify, dan lifetime host bohongan. Dipakai test tiket
/// (<see cref="TicketProcessorTests"/>) dan test alur model (§3/6A2) supaya keduanya menjalankan worker yang sama.
/// </summary>
internal sealed class ScriptedAi(params Func<AiRequest, AiResponse>[] script) : IAiClient
{
    public readonly List<AiRequest> Requests = [];
    private int _next;

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(script[Math.Min(_next++, script.Length - 1)](request));
    }
    public async Task<AiResponse> CompleteStreamingAsync(AiRequest request, Func<string, CancellationToken, Task> onTextDelta,
        CancellationToken ct)
    {
        var response = await CompleteAsync(request, ct);
        if (response.Text.Length > 0) await onTextDelta(response.Text, ct);
        return response;
    }
}

/// <summary>Notify tidak boleh menggagalkan test: semua panggilan ditelan dan bisa diperiksa urutannya.</summary>
internal sealed class NotifyStub : HttpMessageHandler
{
    public readonly ConcurrentQueue<NotifyRequest> Sent = new();
    public readonly ConcurrentQueue<StreamEvent> Streams = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        if (request.RequestUri!.AbsolutePath == "/internal/stream")
            Streams.Enqueue(JsonSerializer.Deserialize<StreamEvent>(body, JsonSerializerOptions.Web)!);
        else
            Sent.Enqueue(JsonSerializer.Deserialize<NotifyRequest>(body, JsonSerializerOptions.Web)!);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}

/// <summary>Satu proses worker berhenti sendiri setelah satu tiket/job; <c>StopApplication</c> cukup dicatat kosong.</summary>
internal sealed class TestLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
    }
}
