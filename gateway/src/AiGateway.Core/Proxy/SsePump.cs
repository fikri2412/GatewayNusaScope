using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core.Common;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Respons SSE upstream yang sudah diterima. Event pertama sudah dibaca (supaya kegagalan di awal stream masih bisa
/// jatuh ke route berikutnya) tetapi belum diteruskan ke klien; sisanya dibaca <see cref="SsePump"/>.
/// </summary>
internal sealed class OpenStream(HttpResponseMessage response, StreamReader reader, IReadOnlyList<string> firstLines) : IDisposable
{
    public StreamReader Reader { get; } = reader;
    public IReadOnlyList<string> FirstLines { get; } = firstLines;

    public void Dispose()
    {
        Reader.Dispose();
        response.Dispose();
    }
}

/// <summary>Bagaimana sebuah stream berakhir; menentukan status dan alasan di <c>usage_logs</c>.</summary>
internal enum StreamEnd { Completed, ClientClosed, Interrupted, Timeout, TooLarge }

/// <summary>Hasil satu stream: akhir, usage terakhir yang dilaporkan upstream, alasan selesai, dan teks jawaban (terpotong).</summary>
internal sealed record StreamOutcome(StreamEnd End, JsonObject? Usage, string? FinishReason, string Content);

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
    /// <param name="max">Batas durasi total stream.</param>
    /// <param name="ct">Token request klien: batal berarti klien memutus koneksi.</param>
    public static async Task<StreamOutcome> RunAsync(
        OpenStream open, Stream output, string alias, bool forwardUsage, TimeSpan idle, TimeSpan max, CancellationToken ct)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(max);
        // Writer sengaja tidak di-dispose: flush dilakukan di batas event, dan dispose akan menulis lagi ke klien yang mungkin sudah putus.
        var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true);

        JsonObject? usage = null;
        string? finish = null;
        var content = new StringBuilder();
        var skipBlank = false;
        var firstIndex = 0;
        var end = StreamEnd.Interrupted;

        async ValueTask<string?> NextAsync()
        {
            if (firstIndex < open.FirstLines.Count) return open.FirstLines[firstIndex++];
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            idleCts.CancelAfter(idle);
            return await open.Reader.ReadLineAsync(idleCts.Token);
        }

        // Mengembalikan true bila stream selesai ([DONE]).
        async Task<bool> HandleAsync(string line)
        {
            if (line.Length == 0) // batas event
            {
                if (skipBlank) { skipBlank = false; return false; }
                await writer.WriteAsync("\n".AsMemory(), ct);
                await writer.FlushAsync(ct);
                return false;
            }
            if (!TryGetData(line, out var payload)) // komentar, event:, id:, retry: diteruskan apa adanya
            {
                await writer.WriteAsync((line + "\n").AsMemory(), ct);
                return false;
            }
            if (payload.Trim() == "[DONE]")
            {
                await writer.WriteAsync("data: [DONE]\n\n".AsMemory(), ct);
                await writer.FlushAsync(ct);
                return true;
            }

            var chunk = TryParse(payload);
            if (chunk is null)
            {
                await writer.WriteAsync((line + "\n").AsMemory(), ct);
                return false;
            }

            if (chunk["usage"] is JsonObject reported)
            {
                usage = (JsonObject)reported.DeepClone();
                if (!forwardUsage)
                {
                    if (chunk["choices"] is not JsonArray { Count: > 0 })
                    {
                        skipBlank = true; // chunk usage-saja: buang beserta baris kosong pemisahnya
                        return false;
                    }
                    chunk.Remove("usage");
                }
            }
            if (chunk["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice)
            {
                if (Str(choice["finish_reason"]) is { } reason) finish = reason;
                if (choice["delta"] is JsonObject delta && Str(delta["content"]) is { } text && content.Length < MaxCapturedChars)
                    content.Append(text, 0, Math.Min(text.Length, MaxCapturedChars - content.Length));
            }
            if (chunk.ContainsKey("model")) chunk["model"] = alias;
            await writer.WriteAsync(("data: " + chunk.ToJsonString() + "\n").AsMemory(), ct);
            return false;
        }

        while (true)
        {
            string? line;
            try { line = await NextAsync(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { end = StreamEnd.ClientClosed; break; }
            catch (OperationCanceledException) { end = StreamEnd.Timeout; break; }
            catch (ResponseTooLargeException) { end = StreamEnd.TooLarge; break; }
            catch (Exception ex) when (ex is IOException or HttpRequestException) { end = StreamEnd.Interrupted; break; }

            if (line is null)
            {
                // Upstream menutup tanpa [DONE]: utuh bila finish_reason sudah terlihat, selain itu terputus.
                if (finish is not null && await TryWriteAsync(writer, "data: [DONE]\n\n", ct)) end = StreamEnd.Completed;
                else if (finish is not null) end = StreamEnd.ClientClosed;
                break;
            }

            try
            {
                if (!await HandleAsync(line)) continue;
                end = StreamEnd.Completed;
                break;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                end = StreamEnd.ClientClosed;
                break;
            }
        }

        if (end is StreamEnd.Interrupted or StreamEnd.Timeout or StreamEnd.TooLarge)
        {
            var (code, message) = end switch
            {
                StreamEnd.Timeout => ("upstream_stream_timeout", "The provider stopped responding mid-stream."),
                StreamEnd.TooLarge => ("upstream_response_too_large", "The provider returned a response that is too large."),
                _ => ("upstream_stream_interrupted", "The provider closed the stream before it finished."),
            };
            // Status 200 sudah terkirim; satu-satunya jalan memberi tahu klien adalah event error lalu menutup.
            await TryWriteAsync(writer, $"data: {GatewayResponse.Error(502, ErrorTypes.Upstream, code, message).Body}\n\n", ct);
        }

        return new StreamOutcome(end, usage, finish, content.ToString());
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

    private static JsonObject? TryParse(string payload)
    {
        try { return JsonNode.Parse(payload) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
