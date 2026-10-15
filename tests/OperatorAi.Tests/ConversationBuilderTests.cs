using System.Text.Json.Nodes;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator;

namespace OperatorAi.Tests;

public class ConversationBuilderTests
{
    private const string U = MessageRoles.User, A = MessageRoles.Assistant;
    private static readonly IReadOnlyDictionary<Guid, Attachment> NoAttachments = new Dictionary<Guid, Attachment>();

    private static Message M(long id, string role, string? content, string? aiJson = null, string? attachmentIds = null) =>
        new() { Id = id, ConversationId = Guid.Empty, Role = role, Content = content, AiContentJson = aiJson, AttachmentIdsJson = attachmentIds };

    private static IReadOnlyList<AiMessage> Build(IEnumerable<Message> rows, int max = 40) =>
        ConversationBuilder.Build(rows, NoAttachments, max);

    private static string ToolCallRow(string callId) => new JsonArray(new AiMessage(A, "", (JsonArray)JsonNode.Parse(
        $$$"""[{"id":"{{{callId}}}","type":"function","function":{"name":"cek_status","arguments":"{}"}}]""")!, ReasoningContent: "mikir").ToJson()).ToJsonString();

    private static string ToolResultRow(string callId, string text) =>
        new JsonArray(new AiMessage("tool", text, ToolCallId: callId).ToJson()).ToJsonString();

    [Fact]
    public void Consecutive_messages_of_the_same_role_become_one_turn()
    {
        var result = Build([M(1, U, "halo"), M(2, U, "satu lagi"), M(3, A, "hai"), M(4, U, "terima kasih")]);

        Assert.Equal(
            [new AiMessage(U, "halo\n\nsatu lagi"), new AiMessage(A, "hai"), new AiMessage(U, "terima kasih")],
            result);
    }

    [Fact]
    public void Adjacent_user_text_does_not_merge_away_a_multimodal_turn()
    {
        const string image = "data:image/png;base64,iVBORw0KGgo=";
        var parts = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "Lihat grafik" },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image } });
        var stored = new JsonArray(new AiMessage(U, null, ContentParts: parts).ToJson()).ToJsonString();
        var result = Build([M(1, U, "Sebelum"), M(2, U, null, stored), M(3, U, "Sesudah")]);

        Assert.Equal(3, result.Count);
        Assert.Equal("Sebelum", result[0].Content);
        Assert.Equal("Sesudah", result[2].Content);
        var content = result[1].ToJson()["content"]!.AsArray();
        Assert.Equal("Lihat grafik", content[0]!["text"]!.GetValue<string>());
        Assert.Equal(image, content[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Cutting_history_never_leaves_an_assistant_turn_first()
    {
        // Batas 4 menyisakan [a1, u2, a2, u3]; a1 di depan dibuang supaya giliran pertama dari user.
        var history = new[] { M(1, U, "u1"), M(2, A, "a1"), M(3, U, "u2"), M(4, A, "a2"), M(5, U, "u3") };

        Assert.Equal([new AiMessage(U, "u2"), new AiMessage(A, "a2"), new AiMessage(U, "u3")], Build(history, 4));
    }

    [Fact]
    public void Only_the_last_N_messages_are_used()
    {
        var history = Enumerable.Range(1, 10).Select(i => M(i, i % 2 == 1 ? U : A, $"pesan {i}")).ToList();

        // Tiga terakhir: id 8 (assistant), 9 (user), 10 (assistant); assistant di depan dibuang.
        Assert.Equal([new AiMessage(U, "pesan 9"), new AiMessage(A, "pesan 10")], Build(history, 3));
    }

    [Fact]
    public void Empty_or_whitespace_rows_are_skipped_and_do_not_split_a_merged_turn()
    {
        Assert.Equal([new AiMessage(U, "a\n\nb")], Build([M(1, U, "a"), M(2, A, null), M(3, A, "   "), M(4, U, "b")]));
    }

    [Fact]
    public void History_without_any_user_message_is_empty()
    {
        Assert.Empty(Build([M(1, A, "halo?")]));
    }

    [Fact]
    public void Tool_call_rows_are_replayed_as_stored_and_never_merged_with_neighbours()
    {
        var result = Build([
            M(1, U, "sudah sampai mana?"),
            M(2, A, null, ToolCallRow("call_1")),
            M(3, U, null, ToolResultRow("call_1", "ExcelRead: Done")),
        ]);

        Assert.Equal([U, A, "tool"], result.Select(m => m.Role));
        var call = result[1];
        Assert.Equal(("mikir", "call_1"), (call.ReasoningContent, result[2].ToolCallId));
        Assert.Equal("cek_status", call.ToolCalls![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("ExcelRead: Done", result[2].Content);
    }

    [Fact]
    public void A_cut_that_starts_at_a_tool_result_drops_it_so_no_orphan_reaches_the_API()
    {
        var rows = new[]
        {
            M(1, U, "u1"), M(2, A, null, ToolCallRow("c1")), M(3, U, null, ToolResultRow("c1", "ok")), M(4, A, "selesai"), M(5, U, "u2"),
        };

        // Batas 3 = [tool, a, u2]: hanya u2 yang giliran user; tool dan assistant di depannya dibuang.
        Assert.Equal([new AiMessage(U, "u2")], Build(rows, 3));
    }

    [Fact]
    public void Data_rows_merge_into_a_neighbouring_user_turn_but_keep_their_text()
    {
        var note = new JsonArray(new AiMessage(U, "hasil job: Analyze selesai, output tersimpan").ToJson()).ToJsonString();

        var result = Build([M(1, U, "analisis dong"), M(2, A, "sedang diproses"), M(3, U, null, note)]);

        Assert.Equal([new AiMessage(U, "analisis dong"), new AiMessage(A, "sedang diproses"), new AiMessage(U, "hasil job: Analyze selesai, output tersimpan")], result);
    }

    [Fact]
    public void An_unanswered_trailing_tool_call_is_dropped_so_no_partial_group_reaches_the_API()
    {
        var rows = new[] { M(1, U, "u1"), M(2, A, null, ToolCallRow("c1")) };

        Assert.Equal([new AiMessage(U, "u1")], Build(rows));
    }

    [Fact]
    public void User_attachments_are_announced_with_their_id_so_the_AI_can_pass_it_to_a_tool()
    {
        var id = Guid.NewGuid();
        var attachments = new Dictionary<Guid, Attachment>
        {
            [id] = new() { Id = id, ConversationId = Guid.Empty, FileName = "data.xlsx", StoragePath = "x", Kind = AttachmentKinds.Upload, ContentType = "x" },
        };

        var result = ConversationBuilder.Build([M(1, U, "tolong analisis", attachmentIds: $"[\"{id}\",\"{Guid.NewGuid()}\"]")], attachments, 40);

        // Id yang tidak ada di daftar lampiran dilewati (bukan error).
        Assert.Equal([new AiMessage(U, $"tolong analisis\n[Lampiran: data.xlsx, attachment_id={id}]")], result);
    }
}
