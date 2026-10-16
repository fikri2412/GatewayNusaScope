using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core.Common;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Respons SSE upstream yang sudah diterima. Event pertama sudah dibaca (supaya kegagalan di awal stream masih bisa
/// jatuh ke route berikutnya) tetapi belum diteruskan ke klien; sisanya dibaca <see cref="SsePump"/>.
/// </summary>
internal sealed class OpenStream(HttpResponseMessage response, TextReader reader, IReadOnlyList<string> firstLines) : IDisposable
{
    public TextReader Reader { get; } = reader;
    public IReadOnlyList<string> FirstLines { get; } = firstLines;

    public void Dispose()
    {
        Reader.Dispose();
        response.Dispose();
    }
}

/// <summary>Bagaimana sebuah stream berakhir; menentukan status dan alasan di <c>usage_logs</c>.</summary>
internal enum StreamEnd { Completed, ClientClosed, Interrupted, Timeout, TooLarge, UpstreamError }

/// <summary>Hasil satu stream: akhir, usage terakhir yang dilaporkan upstream, alasan selesai, dan teks jawaban (terpotong).</summary>
internal sealed record StreamOutcome(StreamEnd End, JsonObject? Usage, string? FinishReason, string Content, bool UsageIncomplete);

/// <summary>
/// Meneruskan SSE upstream ke klien event demi event dalam format OpenAI: field <c>model</c> diganti alias tenant,
/// chunk usage-saja (<c>choices: []</c>) dibuang bila klien tidak memintanya lewat
/// <c>stream_options.include_usage</c> (klien naif biasa membaca <c>choices[0]</c> dan akan error), dan usage terakhir
/// dicatat untuk penagihan. Tidak menyentuh database: pencatatan dilakukan pemanggil dari <see cref="StreamOutcome"/>.
/// </summary>
internal static class SsePump
{
    /// <summary>Batas teks jawaban yang dikumpulkan untuk <c>request_bodies</c> (opt-in); sisanya dibuang.</summary>
    private const int MaxCapturedChars = 100_000;

    public static bool TryGetData(string line, out string payload)
    {
        payload = "";
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return false;
        payload = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
        return true;
    }

    /// <param name="idle">Batas diam antar baris dari upstream.</param>
    /// <param name="max">Batas durasi total stream, termasuk write/flush ke klien dan event error.</param>
    /// <param name="ct">Token request klien: batal berarti klien memutus koneksi.</param>
    public static async Task<StreamOutcome> RunAsync(
        OpenStream open, Stream output, string alias, bool forwardUsage, TimeSpan idle, TimeSpan max, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource();
        using var overall = new CancellationTokenSource();
        // Kestrel bisa membatalkan RequestAborted sebagai efek write yang dibatalkan deadline.
        // Simpan pemicu pertama sebelum token gabungan membatalkan I/O; efek itu bukan disconnect klien.
        var cancellationCause = 0;
        using var clientCancellation = ct.Register(() =>
        {
            Interlocked.CompareExchange(ref cancellationCause, 1, 0);
            overall.Cancel();
        });
        using var deadlineCancellation = deadline.Token.Register(() =>
        {
            Interlocked.CompareExchange(ref cancellationCause, 2, 0);
            overall.Cancel();
        });
        deadline.CancelAfter(max);

        StreamEnd CancellationEnd() => Volatile.Read(ref cancellationCause) == 1 ? StreamEnd.ClientClosed : StreamEnd.Timeout;
        // Writer sengaja tidak di-dispose: flush dilakukan di batas event, dan dispose akan menulis lagi ke klien yang mungkin sudah putus.
        var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true);

        JsonObject? usage = null;
        var usageIncomplete = true;
        string? finish = null;
        var content = new StringBuilder();
        var skipBlank = false;
        var assembler = new SseDataAssembler(); // data: multi-baris (GA-05)
        var firstIndex = 0;
        var end = StreamEnd.Interrupted;

        void MergeUsage(JsonObject reported)
        {
            usage ??= new JsonObject();
            foreach (var pair in reported)
            {
                if (JsonAccess.TokenCount(pair.Value) is { } count) usage[pair.Key] = count;
                else if (pair.Value is JsonObject details)
                {
                    foreach (var detail in details)
                    {
                        if (JsonAccess.TokenCount(detail.Value) is not { } known) continue;
                        if (usage[pair.Key] is not JsonObject target)
                            usage[pair.Key] = target = new JsonObject();
                        target[detail.Key] = known;
                    }
                }
            }
        }

        async ValueTask<string?> NextAsync()
        {
            overall.Token.ThrowIfCancellationRequested();
            if (firstIndex < open.FirstLines.Count) return open.FirstLines[firstIndex++];
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            idleCts.CancelAfter(idle);
            try { return await open.Reader.ReadLineAsync(idleCts.Token); }
            catch (Exception ex) when (ex is IOException or HttpRequestException && idleCts.IsCancellationRequested)
            {
                throw new OperationCanceledException("Upstream read canceled.", ex, idleCts.Token);
            }
        }

        // Sisa JSON multi-baris yang tidak pernah lengkap diteruskan apa adanya, seperti baris data tak valid lainnya.
        async Task FlushLeftoversAsync()
        {
            if (!assembler.HasPending) return;
            foreach (var raw in assembler.TakeLeftovers()) await writer.WriteAsync((raw + "\n").AsMemory(), overall.Token);
        }

        // Mengembalikan true bila stream selesai ([DONE]).
        async Task<bool> HandleAsync(string line)
        {
            if (line.Length == 0) // batas event
            {
                var flushed = assembler.HasPending;
                await FlushLeftoversAsync();
                // Baris kosong milik chunk usage-saja yang dibuang ikut dibuang, kecuali sisa yang baru diteruskan:
                // sisa itu butuh pemisah event, kalau tidak event berikutnya menempel padanya.
                if (skipBlank) { skipBlank = false; if (!flushed) return false; }
                await writer.WriteAsync("\n".AsMemory(), overall.Token);
                await writer.FlushAsync(overall.Token);
                return false;
            }
            if (!TryGetData(line, out var payload)) // komentar, event:, id:, retry: diteruskan apa adanya
            {
                await writer.WriteAsync((line + "\n").AsMemory(), overall.Token);
                return false;
            }
            if (payload.Trim() == "[DONE]")
            {
                await FlushLeftoversAsync();
                await writer.WriteAsync("data: [DONE]\n\n".AsMemory(), overall.Token);
                await writer.FlushAsync(overall.Token);
                return true;
            }

            // Objek utuh dari satu baris atau gabungan beberapa baris data: (JSON multi-baris); selain itu ditahan
            // (JSON belum lengkap) atau diteruskan apa adanya (bukan JSON).
            var chunk = assembler.Offer(line, payload, out var held);
            if (chunk is null)
            {
                if (assembler.Overflowed) await FlushLeftoversAsync();
                else if (!held) await writer.WriteAsync((line + "\n").AsMemory(), overall.Token);
                return false;
            }

            if (chunk["usage"] is JsonObject reported)
            {
                MergeUsage(reported);
                usageIncomplete = JsonAccess.TokenCount(reported["prompt_tokens"]) is null
                    || JsonAccess.TokenCount(reported["completion_tokens"]) is null;
            }
            if (chunk["error"] is not null)
            {
                end = StreamEnd.UpstreamError;
                await writer.WriteAsync(("data: " + chunk.ToJsonString() + "\n\n").AsMemory(), overall.Token);
                await writer.FlushAsync(overall.Token);
                return true; // kegagalan terminal: jangan membaca atau meneruskan [DONE] berikutnya.
            }
            if (!forwardUsage && chunk["usage"] is JsonObject)
            {
                if (chunk["choices"] is not JsonArray { Count: > 0 })
                {
                    skipBlank = true; // chunk usage-saja: buang beserta baris kosong pemisahnya
                    return false;
                }
                chunk.Remove("usage");
            }
            if (chunk["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice)
            {
                if (Str(choice["finish_reason"]) is { } reason) finish = reason;
                if (choice["delta"] is JsonObject delta && Str(delta["content"]) is { } text && content.Length < MaxCapturedChars)
                    content.Append(text, 0, Math.Min(text.Length, MaxCapturedChars - content.Length));
            }
            if (chunk.ContainsKey("model")) chunk["model"] = alias;
            await writer.WriteAsync(("data: " + chunk.ToJsonString() + "\n").AsMemory(), overall.Token);
            return false;
        }

        while (true)
        {
            string? line;
            try { line = await NextAsync(); }
            catch (OperationCanceledException) { end = CancellationEnd(); break; }
            catch (ResponseTooLargeException) { end = StreamEnd.TooLarge; break; }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                end = overall.IsCancellationRequested ? CancellationEnd() : StreamEnd.Interrupted;
                break;
            }

            if (line is null)
            {
                // Event terakhir tanpa baris kosong penutup: sisa yang tertahan tidak boleh hilang.
                if (assembler.HasPending)
                {
                    foreach (var raw in assembler.TakeLeftovers())
                        if (!await TryWriteAsync(writer, raw + "\n", overall.Token)) break;
                }
                // Upstream menutup tanpa [DONE]: utuh bila finish_reason sudah terlihat, selain itu terputus.
                if (finish is not null && await TryWriteAsync(writer, "data: [DONE]\n\n", overall.Token)) end = StreamEnd.Completed;
                else if (finish is not null) end = overall.IsCancellationRequested ? CancellationEnd() : StreamEnd.ClientClosed;
                break;
            }

            try
            {
                if (!await HandleAsync(line)) continue;
                if (end != StreamEnd.UpstreamError) end = StreamEnd.Completed;
                break;
            }
            catch (OperationCanceledException)
            {
                end = CancellationEnd();
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                end = overall.IsCancellationRequested ? CancellationEnd() : StreamEnd.ClientClosed;
                break;
            }
        }

        if (end is StreamEnd.Interrupted or StreamEnd.Timeout or StreamEnd.TooLarge)
        {
            var (code, message) = end switch
            {
                StreamEnd.Timeout => ("upstream_stream_timeout", "The stream exceeded its deadline or the provider stopped responding."),
                StreamEnd.TooLarge => ("upstream_response_too_large", "The provider returned a response that is too large."),
                _ => ("upstream_stream_interrupted", "The provider closed the stream before it finished."),
            };
            // Best effort dalam deadline yang sama; deadline habis tidak boleh memulai write macet baru.
            if (!await TryWriteAsync(writer, $"data: {GatewayResponse.Error(502, ErrorTypes.Upstream, code, message).Body}\n\n", overall.Token))
            {
                if (overall.IsCancellationRequested) end = CancellationEnd();
            }
        }

        // Adaptor bisa menerima usage sebelum sempat mengirim chunk final, termasuk saat ReadLine dibatalkan.
        if (open.Reader is ChatChunkReader translator)
        {
            if (translator.UsageSnapshot is { } snapshot) MergeUsage(snapshot);
            usageIncomplete = translator.UsageIncomplete;
        }

        return new StreamOutcome(end, usage, finish, content.ToString(), usageIncomplete);
    }

    private static async Task<bool> TryWriteAsync(StreamWriter writer, string text, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(text.AsMemory(), ct);
            await writer.FlushAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
