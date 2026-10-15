using Microsoft.EntityFrameworkCore;
using OperatorAi.Api;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

/// <summary>
/// Jawab ulang &amp; bandingkan (§4.3) dari sisi pemakai: satu tiket alternatif per model tanpa pesan user baru,
/// kunci idempotensi klien, dan daftar model yang divalidasi utuh sebelum satu baris pun ditulis.
/// </summary>
public class RegenerateCompareTests : QueueTestBase
{
    private static readonly IReadOnlyList<ModelInfoDto> Catalog =
    [
        new("model-a", true, false, true, 4096, null),
        new("model-b", true, true, true, 4096, null),
    ];

    private static Task<IReadOnlyList<ModelInfoDto>> ModelsAsync(CancellationToken _) => Task.FromResult(Catalog);

    /// <summary>Percakapan dengan satu tanya-jawab selesai: pesan user + tiketnya, lalu balasan assistant.</summary>
    private async Task<(Guid ConversationId, long UserMessageId, long AnswerId)> NewAnsweredAsync(string answerModel = "model-a")
    {
        var conversationId = await NewConversationAsync();
        var (userMessageId, answerId) = await NewAnswerAsync(conversationId, answerModel);
        return (conversationId, userMessageId, answerId);
    }

    /// <summary>Tanya-jawab selesai tambahan di percakapan yang sudah ada (pesan user + tiket done + balasan).</summary>
    private async Task<(long UserMessageId, long AnswerId)> NewAnswerAsync(Guid conversationId, string answerModel = "model-a")
    {
        await using var db = NewContext();
        var user = new Message { ConversationId = conversationId, Role = MessageRoles.User, Content = "halo", ModelAlias = answerModel };
        db.Messages.Add(user);
        await db.SaveChangesAsync();
        var item = new QueueItem
        {
            ConversationId = conversationId, Type = QueueTypes.UserMessage, PayloadJson = PacketJson(answerModel),
            ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = user.Id, Status = Statuses.Done,
        };
        db.QueueItems.Add(item);
        await db.SaveChangesAsync();
        var answer = new Message
        {
            ConversationId = conversationId, Role = MessageRoles.Assistant, Content = "jawaban", ModelAlias = answerModel,
            QueueItemId = item.Id,
        };
        db.Messages.Add(answer);
        await db.SaveChangesAsync();
        return (user.Id, answer.Id);
    }

    private async Task<List<QueueItem>> TurnsAsync(Guid conversationId)
    {
        await using var db = NewContext();
        return await db.QueueItems.AsNoTracking()
            .Where(q => q.ConversationId == conversationId && q.ClientRequestId != null).OrderBy(q => q.Id).ToListAsync();
    }

    private async Task<int> MessageCountAsync(Guid conversationId)
    {
        await using var db = NewContext();
        return await db.Messages.CountAsync(m => m.ConversationId == conversationId);
    }

    [Fact]
    public async Task Regenerate_is_idempotent_for_the_same_key_and_source()
    {
        var (conversationId, userMessageId, answerId) = await NewAnsweredAsync();
        var key = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var first = await ConversationEndpoints.RegenerateTurnAsync(db, "test", conversationId, answerId, key,
                ModelsAsync, Policy(), CancellationToken.None);
            Assert.Equal(202, first.Status);
            Assert.Equal(userMessageId, first.Result!.SourceMessageId);
            Assert.Equal("model-a", first.Result.ModelAlias);

            var replay = await ConversationEndpoints.RegenerateTurnAsync(db, "test", conversationId, answerId, key,
                ModelsAsync, Policy(), CancellationToken.None);
            Assert.Equal(200, replay.Status);
            Assert.Equal(first.Result.QueueItemId, replay.Result!.QueueItemId);
        }

        var turns = await TurnsAsync(conversationId);
        var turn = Assert.Single(turns);
        Assert.Equal(key.ToString(), turn.ClientRequestId);
        Assert.False(turn.IsPrimary);
        Assert.Equal(userMessageId, turn.SourceMessageId);
        var payload = await GetItemAsync(turn.Id);
        Assert.Contains("\"model\":\"model-a\"", payload.PayloadJson);
        Assert.Equal(2, await MessageCountAsync(conversationId)); // tidak ada pesan user baru
    }

    [Fact]
    public async Task Regenerate_uses_the_answers_model_and_the_same_key_on_another_source_conflicts()
    {
        var (conversationId, userMessageId, answerId) = await NewAnsweredAsync();
        var (_, secondAnswer) = await NewAnswerAsync(conversationId, "model-b");
        // Model percakapan berbeda: payload harus mengikuti model jawaban yang dijawab ulang.
        await using (var db = NewContext())
        {
            await db.Conversations.Where(c => c.Id == conversationId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ModelAlias, "model-b"));
        }
        var key = Guid.NewGuid();

        await using var check = NewContext();
        var first = await ConversationEndpoints.RegenerateTurnAsync(check, "test", conversationId, answerId, key,
            ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(202, first.Status);
        Assert.Equal("model-a", first.Result!.ModelAlias);
        Assert.Contains("\"model\":\"model-a\"", (await GetItemAsync(first.Result.QueueItemId)).PayloadJson);
        Assert.Equal(userMessageId, first.Result.SourceMessageId);

        // Kunci sama untuk pesan sumber lain: ditolak, bukan menulis tiket kedua.
        var conflict = await ConversationEndpoints.RegenerateTurnAsync(check, "test", conversationId, secondAnswer, key,
            ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(409, conflict.Status);
        Assert.Equal(ConversationEndpoints.ClientRequestIdConflictCode, conflict.Code);
        Assert.Single(await TurnsAsync(conversationId));
    }

    [Fact]
    public async Task Only_a_visible_answer_of_the_owner_can_be_regenerated()
    {
        var (conversationId, userMessageId, answerId) = await NewAnsweredAsync();

        await using var db = NewContext();
        var userAnchor = await ConversationEndpoints.RegenerateTurnAsync(db, "test", conversationId, userMessageId,
            Guid.NewGuid(), ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(400, userAnchor.Status);
        Assert.Equal(ConversationEndpoints.NotRegenerableCode, userAnchor.Code);

        var foreign = await ConversationEndpoints.RegenerateTurnAsync(db, "user-lain", conversationId, answerId,
            Guid.NewGuid(), ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(404, foreign.Status);
        Assert.Empty(await TurnsAsync(conversationId));
    }

    [Fact]
    public async Task Regenerate_with_a_model_outside_the_catalog_writes_nothing()
    {
        var (conversationId, _, answerId) = await NewAnsweredAsync("model-hilang");

        await using var db = NewContext();
        var outcome = await ConversationEndpoints.RegenerateTurnAsync(db, "test", conversationId, answerId,
            Guid.NewGuid(), ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(400, outcome.Status);
        Assert.Equal(ConversationEndpoints.ModelUnavailableCode, outcome.Code);
        Assert.Empty(await TurnsAsync(conversationId));
    }

    [Fact]
    public async Task Regenerate_of_a_job_callback_answer_is_accepted()
    {
        var (conversationId, userMessageId, _) = await NewAnsweredAsync();
        long answerId;
        await using (var db = NewContext())
        {
            // Jawaban dari job: tiketnya JobCompleted dengan sumber pesan user, bukan tiket UserMessage biasa.
            var callback = new QueueItem
            {
                ConversationId = conversationId, Type = QueueTypes.JobCompleted, PayloadJson = PacketJson(),
                ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = userMessageId, Status = Statuses.Done,
            };
            db.QueueItems.Add(callback);
            await db.SaveChangesAsync();
            var answer = new Message
            {
                ConversationId = conversationId, Role = MessageRoles.Assistant, Content = "hasil analisis",
                ModelAlias = "model-a", QueueItemId = callback.Id,
            };
            db.Messages.Add(answer);
            await db.SaveChangesAsync();
            answerId = answer.Id;
        }

        await using var check = NewContext();
        var outcome = await ConversationEndpoints.RegenerateTurnAsync(check, "test", conversationId, answerId,
            Guid.NewGuid(), ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(202, outcome.Status);
        Assert.Equal(userMessageId, outcome.Result!.SourceMessageId);
    }

    [Fact]
    public async Task Activity_marks_the_regenerated_ticket_as_an_alternative()
    {
        var (conversationId, userMessageId, answerId) = await NewAnsweredAsync();

        await using var db = NewContext();
        var outcome = await ConversationEndpoints.RegenerateTurnAsync(db, "test", conversationId, answerId,
            Guid.NewGuid(), ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(202, outcome.Status);

        var rows = await ConversationEndpoints.ActiveTicketsForOwnerAsync(db, conversationId, "test", CancellationToken.None);
        var row = Assert.Single(rows!);
        Assert.Equal(outcome.Result!.QueueItemId, row.QueueItemId);
        Assert.Equal(Statuses.Pending, row.Status);
        Assert.True(row.IsAlternative);
        Assert.Equal("model-a", row.ModelAlias);
        Assert.Equal(userMessageId, row.SourceMessageId);
    }

    [Fact]
    public async Task Compare_enqueues_one_ticket_per_model_and_replays_the_same_branches()
    {
        var (conversationId, userMessageId, answerId) = await NewAnsweredAsync();
        var key = Guid.NewGuid();
        string[] models = ["model-a", "model-b"];

        await using var db = NewContext();
        var outcome = await ConversationEndpoints.CompareTurnAsync(db, "test", conversationId, answerId, models, key,
            ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(202, outcome.Status);
        Assert.Equal(userMessageId, outcome.Result!.SourceMessageId);
        Assert.Equal(models, outcome.Result.Branches.Select(b => b.ModelAlias));

        var replay = await ConversationEndpoints.CompareTurnAsync(db, "test", conversationId, answerId, models, key,
            ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(200, replay.Status);
        Assert.Equal(outcome.Result.Branches.Select(b => b.QueueItemId), replay.Result!.Branches.Select(b => b.QueueItemId));

        var turns = await TurnsAsync(conversationId);
        Assert.Equal(2, turns.Count);
        Assert.Equal(new[] { $"{key}:0", $"{key}:1" }, turns.Select(t => t.ClientRequestId));
        Assert.All(turns, t =>
        {
            Assert.False(t.IsPrimary);
            Assert.Equal(userMessageId, t.SourceMessageId);
            Assert.Equal(Statuses.Pending, t.Status);
        });
        Assert.Contains("\"model\":\"model-a\"", turns[0].PayloadJson);
        Assert.Contains("\"model\":\"model-b\"", turns[1].PayloadJson);
        Assert.Equal(2, await MessageCountAsync(conversationId));
    }

    [Fact]
    public async Task Compare_on_a_user_message_answers_that_message()
    {
        var (conversationId, userMessageId, _) = await NewAnsweredAsync();

        await using var db = NewContext();
        var outcome = await ConversationEndpoints.CompareTurnAsync(db, "test", conversationId, userMessageId,
            ["model-a"], Guid.NewGuid(), ModelsAsync, Policy(), CancellationToken.None);
        Assert.Equal(202, outcome.Status);
        Assert.Equal(userMessageId, outcome.Result!.SourceMessageId);
    }

    [Fact]
    public async Task Compare_rejects_the_whole_list_when_one_model_fails()
    {
        var (conversationId, _, answerId) = await NewAnsweredAsync();

        await using var db = NewContext();
        var outcome = await ConversationEndpoints.CompareTurnAsync(db, "test", conversationId, answerId,
            ["model-a", "tidak-ada"], Guid.NewGuid(), ModelsAsync, Policy(), CancellationToken.None);

        Assert.Equal(400, outcome.Status);
        Assert.Equal(ConversationEndpoints.CompareInvalidCode, outcome.Code);
        var branch = Assert.Single(outcome.Branches!);
        Assert.Equal("tidak-ada", branch.Model);
        Assert.Equal(ConversationEndpoints.ModelUnavailableCode, branch.Code);
        Assert.Empty(await TurnsAsync(conversationId));
    }
}
