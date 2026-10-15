using System.Text.Json;
using System.Text.Json.Nodes;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Worker.Operator;

/// <summary>Riwayat percakapan → array pesan format OpenAI (DESIGN bagian 8).</summary>
public static class ConversationBuilder
{
    /// <param name="messages">Pesan percakapan urut <c>id</c> naik.</param>
    /// <param name="attachments">Lampiran yang mungkin dirujuk <c>attachment_ids_json</c> pesan user, per id.</param>
    /// <param name="maxMessages"><c>ExecutionPolicy.MaxHistoryMessages</c> tiket yang diklaim: hanya baris terakhir sebanyak ini yang dipakai.</param>
    public static IReadOnlyList<AiMessage> Build(IEnumerable<Message> messages,
        IReadOnlyDictionary<Guid, Attachment> attachments, int maxMessages)
    {
        var merged = new List<AiMessage>();
        foreach (var m in messages.TakeLast(maxMessages))
        foreach (var message in ToAiMessages(m, attachments))
        {
            // Dua pesan teks berturut-turut dengan role sama (user mengirim sebelum balasan datang, atau baris data job
            // di samping pesan user) menjadi satu giliran. Tool call dan hasilnya tidak pernah digabung.
            if (merged.Count > 0 && merged[^1].IsPlainText && message.IsPlainText && merged[^1].Role == message.Role)
                merged[^1] = merged[^1] with { Content = merged[^1].Content + "\n\n" + message.Content };
            else
                merged.Add(message);
        }

        // Pemotongan bisa menyisakan balasan assistant atau hasil tool di depan; giliran pertama harus dari user.
        var firstUser = merged.FindIndex(m => m.Role == MessageRoles.User);
        if (firstUser < 0) return [];
        var result = merged.GetRange(firstUser, merged.Count - firstUser);

        // Panggilan tool tanpa hasilnya tidak boleh dikirim ke API (ditolak 400); grup yang terpotong dibuang seluruhnya.
        if (result.Count > 0 && result[^1].ToolCalls is not null) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static IEnumerable<AiMessage> ToAiMessages(Message m, IReadOnlyDictionary<Guid, Attachment> attachments)
    {
        // Baris dengan pesan mentah (tool call, hasil tool, baris data job): array objek pesan OpenAI, dipakai apa adanya.
        if (!string.IsNullOrEmpty(m.AiContentJson))
        {
            foreach (var node in JsonNode.Parse(m.AiContentJson)!.AsArray())
                yield return AiMessage.FromJson(node!.AsObject());
            yield break;
        }

        if (string.IsNullOrWhiteSpace(m.Content)) yield break;
        var text = m.Content;
        if (m.Role == MessageRoles.User && !string.IsNullOrEmpty(m.AttachmentIdsJson))
            foreach (var id in JsonSerializer.Deserialize<Guid[]>(m.AttachmentIdsJson) ?? [])
                if (attachments.TryGetValue(id, out var a))
                    text += $"\n[Lampiran: {a.FileName}, attachment_id={a.Id}]";
        yield return new AiMessage(m.Role, text);
    }
}
