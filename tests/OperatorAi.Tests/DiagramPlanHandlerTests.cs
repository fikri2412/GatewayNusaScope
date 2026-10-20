using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Documents;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

/// <summary>
/// Job DiagramPlan (6D): sumber PRD selalu dibaca dari baris pesan/dokumen yang diverifikasi server, output AI hanya
/// lolos bila sesuai skema graf, dan keterbatasan dokumen dicatat server — bukan diserahkan ke kepatuhan AI.
/// </summary>
public class DiagramPlanHandlerTests : DocumentTestBase
{
    private const string Prd = "PRD pembayaran: pengguna memilih metode bayar. Bila saldo kurang dari Rp5.000.000 maka pembayaran ditolak.";

    private const string ValidGraph = """
        {"judul":"Alur pembayaran","ringkasan":"Pembayaran diperiksa saldonya.",
         "nodes":[{"id":"n1","kind":"start","label":"Pilih metode bayar"},{"id":"n2","kind":"decision","label":"Saldo cukup?"},
                  {"id":"n3","kind":"end","label":"Selesai"}],
         "edges":[{"from":"n1","to":"n2","label":""},{"from":"n2","to":"n3","label":"Ya"}],
         "catatan":["Alur saat saldo kurang belum dijelaskan."]}
        """;

    private DiagramPlanHandler Handler(IAiClient ai) => new(ai, DbOptions, NullLogger<DiagramPlanHandler>.Instance);

    private static AiResponse Answer(string text, string? finish = "stop") =>
        new(text, finish, 100, 20, new AiMessage("assistant", text), true);

    private static string PlanInput(long? sourceMessageId = null, string tujuan = "Seluruh alur", string judul = "Alur Pembayaran") =>
        JsonSerializer.Serialize(new { tujuan, judul, sourceMessageId });

    private static JsonObject Graph(string json) => JsonNode.Parse(json)!.AsObject();

    private async Task<long> MessageAsync(Guid conversation, string content, string role = MessageRoles.User)
    {
        await using var db = NewContext();
        var message = new Message { ConversationId = conversation, Role = role, Content = content, ModelAlias = TestModel };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    private static string DocumentOutput(string[] pageTexts, string[]? warnings = null) => DocumentReadOutput.Serialize(new DocumentReadOutput(
        Guid.NewGuid().ToString(), "prd.pdf", DocumentReadOutput.KindPdf, pageTexts.Length,
        pageTexts.Select((t, i) => new DocumentPageText(i + 1, t)).ToArray(), [], pageTexts.Sum(t => t.Length), warnings ?? []));

    [Fact]
    public async Task A_PRD_in_the_request_message_is_the_only_source_and_the_graph_is_returned()
    {
        var conversation = await NewConversationAsync();
        var context = await NewJobContextAsync(conversation, JobTypes.DiagramPlan, PlanInput(), goal: Prd);
        context.Job.InputJson = PlanInput(context.SourceMessageId);
        var ai = new ScriptedAi(_ => Answer(ValidGraph));

        var output = await Handler(ai).HandleAsync(context, CancellationToken.None);

        var graph = DiagramGraph.Parse(output);
        Assert.Equal(["n1", "n2", "n3"], graph.Nodes.Select(n => n.Id));
        var prompt = Assert.Single(ai.Requests).Messages[0].Content!;
        Assert.Contains(Prd, prompt);
        Assert.Contains("Seluruh alur", prompt);
        Assert.DoesNotContain("Permintaan asli user", prompt);              // pesan yang sama tidak dikirim dua kali
        Assert.DoesNotContain(Prd, ai.Requests[0].System);                  // isi PRD data, bukan instruksi sistem
        Assert.True(context.Tokens.Total > 0);
    }

    [Fact]
    public async Task An_earlier_PRD_message_is_sent_together_with_the_current_request_as_data()
    {
        var conversation = await NewConversationAsync();
        var prd = await MessageAsync(conversation, Prd);
        var context = await NewJobContextAsync(conversation, JobTypes.DiagramPlan, PlanInput(prd), goal: "gambarkan alur dari PRD tadi");
        var ai = new ScriptedAi(_ => Answer(ValidGraph));

        await Handler(ai).HandleAsync(context, CancellationToken.None);

        var prompt = ai.Requests[0].Messages[0].Content!;
        Assert.Contains("Permintaan asli user", prompt);
        Assert.Contains("gambarkan alur dari PRD tadi", prompt);
        Assert.Contains(Prd, prompt);
    }

    [Fact]
    public async Task A_source_message_from_another_conversation_or_from_the_assistant_is_never_read()
    {
        var mine = await NewConversationAsync();
        var theirs = await NewConversationAsync();
        var foreign = await MessageAsync(theirs, "RAHASIA pihak lain " + Prd);
        var assistant = await MessageAsync(mine, "Jawaban asisten " + Prd, MessageRoles.Assistant);
        var ai = new ScriptedAi(_ => Answer(ValidGraph));

        foreach (var id in new[] { foreign, assistant, 999_999L })
        {
            var context = await NewJobContextAsync(mine, JobTypes.DiagramPlan, PlanInput(id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(context, CancellationToken.None));
        }

        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task A_PDF_source_sends_page_text_and_keeps_server_notes_even_when_the_AI_fills_the_note_limit()
    {
        var conversation = await NewConversationAsync();
        var dependency = DocumentOutput(["Langkah 1: pengguna login.", "", "Langkah 3: pengguna logout."],
            ["Halaman 2 tidak memuat teks yang dapat diekstrak (kemungkinan scan atau gambar) dan tidak dibaca."]);
        var crowded = Graph(ValidGraph);
        crowded["catatan"] = new JsonArray(Enumerable.Range(1, DiagramGraph.MaxNotes).Select(i => (JsonNode?)$"Catatan AI {i}").ToArray());
        var ai = new ScriptedAi(_ => Answer(crowded.ToJsonString()));
        var context = await NewJobContextAsync(conversation, JobTypes.DiagramPlan, PlanInput(), dependency, goal: "buat alur dari PDF ini");

        var graph = DiagramGraph.Parse(await Handler(ai).HandleAsync(context, CancellationToken.None));

        var prompt = ai.Requests[0].Messages[0].Content!;
        Assert.Contains("--- Halaman 1 ---", prompt);
        Assert.Contains("Langkah 3: pengguna logout.", prompt);
        Assert.DoesNotContain("--- Halaman 2 ---", prompt);                // halaman tanpa teks tidak dikirim
        Assert.Contains("buat alur dari PDF ini", prompt);
        Assert.Equal(DiagramGraph.MaxNotes, graph.Catatan.Count);
        Assert.StartsWith("Halaman 2 tidak memuat teks", graph.Catatan[^1]);
        Assert.Equal("Catatan AI 1", graph.Catatan[0]);
    }

    [Fact]
    public async Task A_PDF_without_extractable_text_fails_as_invalid_input_before_any_AI_call()
    {
        var conversation = await NewConversationAsync();
        var dependency = DocumentOutput(["", ""], ["Halaman 1, 2 tidak memuat teks yang dapat diekstrak."]);
        var ai = new ScriptedAi(_ => Answer(ValidGraph));
        var context = await NewJobContextAsync(conversation, JobTypes.DiagramPlan, PlanInput(), dependency);

        var error = await Assert.ThrowsAsync<DocumentInputException>(() => Handler(ai).HandleAsync(context, CancellationToken.None));

        Assert.Contains("tidak memuat teks", error.Message);
        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task Invalid_output_is_repaired_once_with_the_previous_answer_and_a_second_failure_fails_the_job()
    {
        var conversation = await NewConversationAsync();
        var invalid = """{"judul":"x","nodes":[]}""";
        var repaired = new ScriptedAi(_ => Answer(invalid), _ => Answer(ValidGraph));
        var context = await NewJobContextAsync(conversation, JobTypes.DiagramPlan, PlanInput(), goal: Prd);
        context.Job.InputJson = PlanInput(context.SourceMessageId);

        await Handler(repaired).HandleAsync(context, CancellationToken.None);

        Assert.Equal(2, repaired.Requests.Count);
        Assert.Equal(DiagramPlanHandler.RepairInstruction, repaired.Requests[1].Messages[^1].Content);
        Assert.Equal(invalid, repaired.Requests[1].Messages[1].Content);

        var failing = new ScriptedAi(_ => Answer(invalid), _ => Answer(invalid), _ => Answer(ValidGraph));
        await Assert.ThrowsAsync<FormatException>(() => Handler(failing).HandleAsync(context, CancellationToken.None));
        Assert.Equal(2, failing.Requests.Count);                            // tidak ada percobaan ketiga
    }

    [Fact]
    public async Task A_graph_that_smuggles_mermaid_code_is_rejected_and_repaired()
    {
        var conversation = await NewConversationAsync();
        var smuggled = Graph(ValidGraph);
        smuggled["mermaid"] = "flowchart TD\nclick A href \"http://evil.test\"";
        var ai = new ScriptedAi(_ => Answer(smuggled.ToJsonString()), _ => Answer(ValidGraph));
        var context = await NewJobContextAsync(conversation, JobTypes.DiagramPlan, PlanInput(), goal: Prd);
        context.Job.InputJson = PlanInput(context.SourceMessageId);

        var output = await Handler(ai).HandleAsync(context, CancellationToken.None);

        Assert.Equal(2, ai.Requests.Count);
        Assert.DoesNotContain("mermaid", output);
    }

    [Theory]
    [InlineData("""{"judul":"Alur"}""")]                                      // tujuan hilang
    [InlineData("""{"tujuan":"x"}""")]                                        // judul hilang
    [InlineData("""{"tujuan":"x","judul":"y","sourceMessageId":"abc"}""")]    // id bukan angka
    public async Task Malformed_job_input_fails_before_any_AI_call(string input)
    {
        var conversation = await NewConversationAsync();
        var ai = new ScriptedAi(_ => Answer(ValidGraph));
        var context = await NewJobContextAsync(conversation, JobTypes.DiagramPlan, input);

        await Assert.ThrowsAsync<FormatException>(() => Handler(ai).HandleAsync(context, CancellationToken.None));
        Assert.Empty(ai.Requests);
    }
}
