using System.Text.Json.Nodes;
using OperatorAi.Shared.Options;

namespace OperatorAi.Shared.Ai;

/// <summary>
/// Satu pesan format OpenAI. <see cref="ToolCalls"/> disimpan sebagai JSON mentah supaya bisa disimpan di
/// <c>ai_content_json</c> dan dikirim ulang apa adanya (termasuk field tambahan seperti <c>index</c>).
/// </summary>
/// <param name="Role">"user", "assistant", atau "tool" (system prompt dikirim lewat <see cref="AiRequest.System"/>).</param>
/// <param name="ToolCallId">Hanya untuk role "tool": id <c>tool_call</c> yang dijawab.</param>
/// <param name="ReasoningContent">Berpikir model; tidak pernah ditampilkan, hanya dikirim ulang bersama tool call.</param>
/// <param name="ContentParts">Larik bagian konten Chat Completions (teks + <c>image_url</c> data URL) apa adanya;
/// <see cref="Content"/> NULL bila larik ini dipakai. Bagian disimpan mentah supaya bisa dikirim ulang tanpa flattening.</param>
public sealed record AiMessage(string Role, string? Content, JsonArray? ToolCalls = null, string? ToolCallId = null,
    string? ReasoningContent = null, JsonArray? ContentParts = null)
{
    /// <summary>
    /// Teks biasa user/assistant: satu-satunya jenis yang boleh digabung dengan tetangganya. Pesan berlampiran
    /// konten (gambar) tidak pernah digabung supaya bagian-bagiannya tetap utuh.
    /// </summary>
    public bool IsPlainText => ToolCalls is null && ToolCallId is null && ContentParts is null && Role is "user" or "assistant";

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["role"] = Role };
        o["content"] = ContentParts is not null ? ContentParts.DeepClone() : Content ?? "";
        if (ToolCalls is not null) o["tool_calls"] = ToolCalls.DeepClone();
        if (ToolCallId is not null) o["tool_call_id"] = ToolCallId;
        if (ReasoningContent is not null) o["reasoning_content"] = ReasoningContent;
        return o;
    }

    public static AiMessage FromJson(JsonObject o) => new(
        o["role"]?.GetValue<string>() ?? throw new FormatException("pesan AI tanpa role"),
        o["content"] is JsonValue { } c && c.TryGetValue<string>(out var text) ? text : null,
        o["tool_calls"] is JsonArray { Count: > 0 } calls ? (JsonArray)calls.DeepClone() : null,
        o["tool_call_id"]?.GetValue<string>(),
        o["reasoning_content"] is JsonValue { } r && r.TryGetValue<string>(out var reasoning) ? reasoning : null,
        // Larik bagian (mis. teks + gambar) dipertahankan apa adanya; bukan bagian teks biasa yang boleh digabung.
        o["content"] is JsonArray { Count: > 0 } parts ? (JsonArray)parts.DeepClone() : null);
}

/// <param name="Limits">Batas runtime (timeout/byte) snapshot pekerjaan untuk panggilan ini: satu panggilan selalu memakai
/// snapshotnya sendiri, bukan setting proses yang berlaku saat itu. Null hanya untuk pemakaian langsung tingkat rendah
/// di luar alur pekerjaan; klien lalu memakai opsi proses <c>Ai:</c> sebagai gantinya.</param>
public sealed record AiRequest(string Model, Guid ConversationId, string System, IReadOnlyList<AiMessage> Messages, int? MaxTokens,
    JsonArray? Tools = null, string? Tags = null, AiRuntimeLimits? Limits = null, string? ReasoningEffort = null);

public sealed record AiToolCall(string Id, string Name, string Arguments);

/// <param name="Text">Isi jawaban (bukan <c>reasoning_content</c>).</param>
/// <param name="FinishReason"><c>stop</c>, <c>tool_calls</c>, <c>length</c>, dst. dari respons.</param>
/// <param name="Message">Pesan assistant utuh (untuk disimpan dan dikirim ulang bila berisi tool call).</param>
public sealed record AiResponse(string Text, string? FinishReason, int PromptTokens, int CompletionTokens,
    AiMessage Message, bool UsageAvailable)
{
    public IReadOnlyList<AiToolCall> ToolCalls { get; } = ParseToolCalls(Message.ToolCalls);

    private static List<AiToolCall> ParseToolCalls(JsonArray? calls) =>
        calls is null ? [] : calls.Select(c => new AiToolCall(
            c?["id"]?.GetValue<string>() ?? "",
            c?["function"]?["name"]?.GetValue<string>() ?? "",
            c?["function"]?["arguments"]?.GetValue<string>() ?? "")).ToList();
}

public interface IAiClient
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct);

    /// <summary>
    /// Varian <c>stream=true</c>: <paramref name="onTextDelta"/> dipanggil untuk tiap potongan teks publik
    /// (<c>delta.content</c> saja, bukan reasoning/argumen tool) selagi body masih dibaca, dan hasil akhirnya
    /// setara <see cref="CompleteAsync"/>. Respons hanya dianggap utuh bila <c>[DONE]</c> dan <c>finish_reason</c>
    /// sudah terlihat; kegagalan di tengah stream melempar <see cref="AiStreamException"/>.
    /// </summary>
    Task<AiResponse> CompleteStreamingAsync(AiRequest request, Func<string, CancellationToken, Task> onTextDelta,
        CancellationToken ct);
}

/// <summary>Panggilan AI gagal (status bukan 2xx setelah retry, atau respons tidak bisa dibaca).</summary>
public class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Stream AI gagal/terputus setelah request terkirim. Usage yang sempat terlihat dibawa supaya pemanggil bisa
/// mencatatnya sekali; tanpa chunk usage angkanya 0 dengan <see cref="UsageAvailable"/> false (belum diketahui,
/// bukan klaim nol token). <see cref="RequestCanceled"/> true berarti <c>CancellationToken</c> pemanggil yang
/// membatalkan, bukan batas stream; pemanggil boleh melempar ulang cancellation setelah mencatat usage.
/// </summary>
public sealed class AiStreamException(string message, int promptTokens, int completionTokens, bool usageAvailable,
    bool requestCanceled, Exception? inner = null) : AiException(message, inner)
{
    public int PromptTokens { get; } = promptTokens;
    public int CompletionTokens { get; } = completionTokens;
    public bool UsageAvailable { get; } = usageAvailable;
    public bool RequestCanceled { get; } = requestCanceled;
}

/// <summary>Model dicabut/tidak diizinkan gateway; hentikan workflow tanpa retry atau mengganti alias.</summary>
public sealed class AiModelUnavailableException(string model)
    : AiException($"Model '{model}' tidak tersedia atau tidak lagi diizinkan. Pilih model lain untuk pesan baru.")
{
    public string Model { get; } = model;
}

/// <summary>
/// Penyedia AI tidak mengirim byte pertama dalam batas <c>Ai:StreamFirstByteSeconds</c> snapshot panggilan ini. Mengulang langsung hampir pasti
/// menunggu antrean penyedia dari nol, jadi tiket Operator dihentikan dengan balasan jelas dan user mengirim ulang.
/// </summary>
public sealed class AiFirstByteTimeoutException(int seconds)
    : AiException($"Penyedia AI tidak mulai menjawab dalam {seconds} detik.")
{
    public int Seconds { get; } = seconds;
}
