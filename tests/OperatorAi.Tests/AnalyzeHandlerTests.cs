using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

/// <summary>
/// Job Analyze (DESIGN bagian 11.2): output AI divalidasi sebagai input tidak tepercaya, satu kali perbaikan, dan
/// anggaran token percakapan (token di DB + meter lokal job ini) diperiksa sebelum tiap panggilan AI.
/// </summary>
public class AnalyzeHandlerTests : QueueTestBase
{
    private const string Profil = """{"fileName":"penjualan.xlsx","sheets":[]}""";
    private const string Tujuan = "Saya ingin tahu total penjualan";

    private const string ValidJson =
        """{"judul":"Analisis","ringkasan":"Ringkasan","temuan_masalah":[],"rekomendasi_digitalisasi":[],"rancangan_tabel":[],"catatan":""}""";

    private const string InvalidPriorityJson =
        """{"judul":"Analisis","ringkasan":"r","temuan_masalah":[],"rekomendasi_digitalisasi":[{"langkah":"l","manfaat":"m","prioritas":"Tinggi"}],"rancangan_tabel":[],"catatan":""}""";

    private sealed class ScriptedAi(params string[] answers) : IAiClient
    {
        public readonly List<AiRequest> Requests = [];
        public bool UsageAvailable { get; init; } = true;
        public int PromptTokens { get; init; } = 10;
        public int CompletionTokens { get; init; } = 5;

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var text = answers[Math.Min(Requests.Count - 1, answers.Length - 1)];
            return Task.FromResult(new AiResponse(text, "stop", PromptTokens, CompletionTokens, new AiMessage("assistant", text), UsageAvailable));
        }

        public async Task<AiResponse> CompleteStreamingAsync(AiRequest request, Func<string, CancellationToken, Task> onTextDelta,
            CancellationToken ct)
        {
            var response = await CompleteAsync(request, ct);
            await onTextDelta(response.Text, ct);
            return response;
        }
    }

    private async Task<JobContext> NewAnalyzeAsync(string tujuan = Tujuan, string? profil = Profil, ExecutionPolicy? policy = null,
        string? sourceGoal = null, string model = TestModel)
    {
        var conversation = await NewConversationAsync();
        await using var db = NewContext();
        var source = new Message { ConversationId = conversation, Role = MessageRoles.User, Content = sourceGoal ?? tujuan, ModelAlias = model };
        db.Messages.Add(source);
        await db.SaveChangesAsync();
        var job = new Job
        {
            ConversationId = conversation, JobType = JobTypes.Analyze,
            InputJson = JsonSerializer.Serialize(new { tujuan, model }, JsonSerializerOptions.Web),
            ExecutionPolicyJson = (policy ?? Policy()).ToJson(), SourceMessageId = source.Id,
        };
        return new JobContext(job, profil, policy ?? Policy(), source.Id, model);
    }

    private AnalyzeHandler Handler(IAiClient ai) => new(ai, DbOptions, NullLogger<AnalyzeHandler>.Instance);

    [Fact]
    public async Task The_original_user_message_is_used_verbatim_as_the_goal()
    {
        const string goal = "Tolong hitung total penjualan per gudang, lalu tunjukkan tren bulanannya.";
        var ai = new ScriptedAi(ValidJson);
        var ctx = await NewAnalyzeAsync(tujuan: "ringkas penjualan", sourceGoal: goal);

        await Handler(ai).HandleAsync(ctx, CancellationToken.None);

        var prompt = Assert.Single(ai.Requests[0].Messages).Content!;
        Assert.Contains(goal, prompt);                                       // permintaan asli utuh, bukan ringkasan model
        Assert.Contains("ringkas penjualan", prompt);                        // fokus usulan Operator tetap disertakan sebagai data
        Assert.DoesNotContain(goal, ai.Requests[0].System);                  // tujuan user tidak pernah jadi instruksi tepercaya
    }

    [Fact]
    public async Task A_job_without_its_source_message_fails_before_any_ai_call()
    {
        var ai = new ScriptedAi(ValidJson);
        var ctx = await NewAnalyzeAsync();
        var missing = ctx with { SourceMessageId = ctx.SourceMessageId + 1000 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(missing, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task Valid_answer_is_returned_without_a_repair_call()
    {
        var policy = Policy(maxOutputTokens: 777);
        var ai = new ScriptedAi(ValidJson);

        var output = await Handler(ai).HandleAsync(await NewAnalyzeAsync(policy: policy), CancellationToken.None);

        Assert.Single(ai.Requests);
        Assert.Equal(777, ai.Requests[0].MaxTokens);                        // anggaran output dari snapshot job
        Assert.Contains("conversation=", ai.Requests[0].Tags);
        var parsed = AnalysisResult.Parse(output);
        Assert.Equal(("Analisis", "Ringkasan", ""), (parsed.Judul, parsed.Ringkasan, parsed.Catatan));
    }

    [Fact]
    public async Task Ai_call_uses_the_model_of_the_job_it_belongs_to()
    {
        var ai = new ScriptedAi(ValidJson);

        await Handler(ai).HandleAsync(await NewAnalyzeAsync(model: "model-A"), CancellationToken.None);

        Assert.Equal("model-A", Assert.Single(ai.Requests).Model);
    }

    [Theory]
    [InlineData("Ini bukan JSON.")]
    [InlineData(InvalidPriorityJson)]
    public async Task Invalid_content_is_repaired_once_with_prior_answer_and_instruction(string first)
    {
        var ai = new ScriptedAi(first, ValidJson);

        var output = await Handler(ai).HandleAsync(await NewAnalyzeAsync(), CancellationToken.None);

        Assert.Equal(2, ai.Requests.Count);
        Assert.Equal("Analisis", AnalysisResult.Parse(output).Judul);
    }

    [Fact]
    public async Task Tokens_and_usage_of_every_AI_call_are_reported_even_when_the_job_still_fails()
    {
        var ctx = await NewAnalyzeAsync();

        await Assert.ThrowsAsync<FormatException>(() => Handler(new ScriptedAi("bukan json", "tetap bukan json")).HandleAsync(ctx, CancellationToken.None));

        Assert.Equal(30, ctx.Tokens.Total);                                 // awal + perbaikan, masing-masing 10 + 5
        Assert.True(ctx.Tokens.UsageAvailable);
    }

    [Fact]
    public async Task Usage_that_the_provider_does_not_report_leaves_the_meter_incomplete()
    {
        var ctx = await NewAnalyzeAsync();

        await Handler(new ScriptedAi(ValidJson) { UsageAvailable = false }).HandleAsync(ctx, CancellationToken.None);

        Assert.False(ctx.Tokens.UsageAvailable);
    }

    [Fact]
    public async Task Still_invalid_after_the_single_repair_fails_the_job()
    {
        var ai = new ScriptedAi("bukan json", """{"judul":"j"}""");

        await Assert.ThrowsAsync<FormatException>(async () => await Handler(ai).HandleAsync(await NewAnalyzeAsync(), CancellationToken.None));

        Assert.Equal(2, ai.Requests.Count); // tidak ada percobaan ketiga
    }

    [Fact]
    public async Task A_conversation_at_the_token_limit_never_calls_the_AI()
    {
        var ctx = await NewAnalyzeAsync(policy: Policy(maxTokensPerConversation: 1000));
        await using (var db = NewContext()) await db.AddTokensAsync(ctx.Job.ConversationId, 1000, usageAvailable: true, CancellationToken.None);
        var ai = new ScriptedAi(ValidJson);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(ctx, CancellationToken.None));

        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task The_repair_call_is_skipped_when_the_budget_is_exhausted()
    {
        var ctx = await NewAnalyzeAsync(policy: Policy(maxTokensPerConversation: 1000));
        await using (var db = NewContext()) await db.AddTokensAsync(ctx.Job.ConversationId, 990, usageAvailable: true, CancellationToken.None);
        var ai = new ScriptedAi("bukan json", ValidJson);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(ai).HandleAsync(ctx, CancellationToken.None));

        Assert.Single(ai.Requests);                                         // perbaikan tidak dijalankan
        Assert.Equal(15, ctx.Tokens.Total);
    }

    [Fact]
    public async Task Tujuan_is_validated_before_any_ai_call()
    {
        var ai = new ScriptedAi(ValidJson);

        await Assert.ThrowsAsync<FormatException>(async () => await Handler(ai).HandleAsync(await NewAnalyzeAsync(tujuan: " \t "), CancellationToken.None));
        await Assert.ThrowsAsync<FormatException>(async () =>
            await Handler(ai).HandleAsync(await NewAnalyzeAsync(tujuan: new string('x', AnalyzeHandler.MaxTujuanChars + 1)), CancellationToken.None));
        Assert.Empty(ai.Requests);

        // Batas 2000 karakter masih diterima.
        await Handler(ai).HandleAsync(await NewAnalyzeAsync(tujuan: new string('x', AnalyzeHandler.MaxTujuanChars)), CancellationToken.None);
        Assert.Single(ai.Requests);
    }

    [Fact]
    public async Task Malformed_input_or_missing_dependency_fails_before_any_ai_call()
    {
        var ai = new ScriptedAi(ValidJson);
        var good = await NewAnalyzeAsync();
        var malformed = good with { Job = new Job { ConversationId = good.Job.ConversationId, JobType = JobTypes.Analyze, InputJson = """{"tujuan":5}""" } };

        await Assert.ThrowsAsync<FormatException>(() => Handler(ai).HandleAsync(malformed, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Handler(ai).HandleAsync(await NewAnalyzeAsync(profil: null), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Handler(ai).HandleAsync(await NewAnalyzeAsync(profil: "  "), CancellationToken.None));
        Assert.Empty(ai.Requests);
    }
}
