using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core.Domain;

namespace AiGateway.Core.Proxy;

/// <summary>
/// Penerjemah antara Chat Completions (format klien) dan satu family API upstream non-chat. Seluruh pipeline
/// (kebijakan, fallback, usage, harga) bekerja pada bentuk OpenAI sesudah terjemahan.
/// </summary>
internal interface IFamilyAdapter
{
    /// <summary>Request upstream dari body Chat Completions; false + <paramref name="error"/> bila ada konten tanpa padanan.</summary>
    bool TryToRequest(JsonObject chat, string upstreamModel, bool stream, int? modelMaxOutput, out JsonObject request, out string error);

    /// <summary>URL endpoint family ini dari base URL provider.</summary>
    Uri RequestUri(Uri baseUri);

    void AddHeaders(HttpRequestMessage request);

    /// <summary>Respons utuh upstream menjadi <c>chat.completion</c>; null bila bentuknya bukan respons yang valid.</summary>
    JsonObject? ToChatCompletion(JsonObject response);

    /// <summary>Pembaca baris SSE upstream yang menyajikan SSE Chat Completions.</summary>
    ChatChunkReader TranslateStream(TextReader upstream);
}

internal static class FamilyAdapters
{
    /// <summary>Adaptor untuk family ini, atau null bila upstream sudah berbicara Chat Completions.</summary>
    public static IFamilyAdapter? For(string? family) => family switch
    {
        ApiFamilies.AnthropicMessages => AnthropicAdapter.Instance,
        ApiFamilies.OpenAiResponses => ResponsesAdapter.Instance,
        _ => null,
    };
}

/// <summary>
/// Membaca SSE upstream non-chat dan menyajikannya baris demi baris sebagai SSE Chat Completions
/// (<c>chat.completion.chunk</c>, chunk usage-saja, <c>[DONE]</c>), sehingga <see cref="SsePump"/> tidak perlu tahu
/// family upstream. Turunan menerjemahkan satu event upstream per <see cref="OnEvent"/>.
/// </summary>
internal abstract class ChatChunkReader(TextReader upstream) : TextReader
{
    private readonly Queue<string> _lines = new();
    private bool _done;
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    protected string? Id, Finish;
    protected string Model = "";
    protected bool Started;
    /// <summary>Usage berbentuk OpenAI; dikirim sebagai chunk usage-saja saat stream selesai.</summary>
    protected JsonObject? Usage;

    /// <summary>
    /// Usage terakhir yang benar-benar dilaporkan upstream (bentuk OpenAI), hanya field yang sudah diketahui — field
    /// yang belum pernah dilaporkan tidak diisi nol. Dipakai pencatat untuk mempertahankan angka yang diketahui saat
    /// stream terputus sebelum <see cref="Complete"/> (mis. disconnect/timeout).
    /// </summary>
    public JsonObject? UsageSnapshot => Usage;

    /// <summary>
    /// True selama event usage final family ini belum terlihat (Anthropic: <c>message_delta</c>; Responses:
    /// <c>response.completed</c>/<c>response.incomplete</c>). Output parsial dari <c>message_start</c> tidak
    /// dianggap bukti kelengkapan.
    /// </summary>
    public bool UsageIncomplete { get; protected set; } = true;

    /// <summary>
    /// Gabungkan laporan usage yang sudah dinormalisasi ke <see cref="Usage"/> per field: laporan parsial atau rusak
    /// tidak menghapus angka yang sudah diketahui, dan field yang tidak dilaporkan tidak menjadi nol.
    /// </summary>
    protected void MergeReportedUsage(JsonObject normalized)
    {
        Usage ??= new JsonObject();
        foreach (var pair in normalized) Usage[pair.Key] = pair.Value?.DeepClone();
    }

    protected abstract void OnEvent(JsonObject e);

    /// <summary>Terjemahkan satu baris mentah upstream; hasilnya antre untuk <see cref="ReadLineAsync(CancellationToken)"/>.</summary>
    public void Feed(string raw)
    {
        if (_done || !SsePump.TryGetData(raw, out var payload)) return; // event:, id:, komentar, baris kosong
        JsonObject? e;
        try { e = JsonNode.Parse(payload) as JsonObject; }
        catch (JsonException) { return; }
        if (e is not null) OnEvent(e);
    }

    /// <summary>Baris terjemahan yang sudah antre (dipakai untuk event pertama yang dibaca sebelum commit).</summary>
    public List<string> Drain()
    {
        var lines = _lines.ToList();
        _lines.Clear();
        return lines;
    }

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_lines.TryDequeue(out var line)) return line;
            if (_done) return null;
            var raw = await upstream.ReadLineAsync(cancellationToken);
            if (raw is null)
            {
                // Upstream menutup tanpa event akhir: utuh hanya bila alasan selesai sudah terlihat.
                if (Started && Finish is not null) { Complete(); continue; }
                return null;
            }
            Feed(raw);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) upstream.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Chunk pembuka (role assistant); dipanggil sekali sebelum konten pertama.</summary>
    protected void EnsureStarted()
    {
        if (Started) return;
        Started = true;
        Emit(Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }, null));
    }

    protected JsonObject Chunk(JsonObject delta, string? finish) => new()
    {
        ["id"] = Id, ["object"] = "chat.completion.chunk", ["created"] = _created, ["model"] = Model,
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
    };

    protected void Emit(JsonObject chunk)
    {
        _lines.Enqueue("data: " + chunk.ToJsonString());
        _lines.Enqueue("");
    }

    protected void EmitError(JsonNode? error) => Emit(new JsonObject { ["error"] = error?.DeepClone() });

    /// <summary>Chunk usage-saja (bila ada) lalu <c>[DONE]</c>.</summary>
    protected void Complete()
    {
        if (_done) return;
        _done = true;
        if (Usage is not null)
            Emit(new JsonObject
            {
                ["id"] = Id, ["object"] = "chat.completion.chunk", ["created"] = _created, ["model"] = Model,
                ["choices"] = new JsonArray(), ["usage"] = Usage,
            });
        _lines.Enqueue("data: [DONE]");
        _lines.Enqueue("");
    }
}
