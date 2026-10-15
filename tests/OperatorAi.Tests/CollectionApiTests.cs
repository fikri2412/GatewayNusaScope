using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Api;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

/// <summary>
/// Koleksi jawaban tersimpan (§4.4) dari sisi pemakai: hanya jawaban assistant yang terlihat dan masih milik user
/// sendiri yang bisa disimpan, simpan ulang tidak menggandakan, dan sesi yang dihapus tidak menghapus snapshot.
/// </summary>
public class CollectionApiTests : QueueTestBase, IDisposable
{
    private readonly string _root = ConversationTestSupport.NewTempRoot();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Percakapan berjudul + pesan user + jawaban assistant yang terlihat, seperti setelah satu kiriman.</summary>
    private async Task<(Guid ConversationId, long UserMessageId, long AnswerId)> NewAnswerAsync(string owner = "test")
    {
        await using var db = NewContext();
        var conversation = new Conversation { UserId = owner, Title = "judul sesi", ModelAlias = TestModel };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        var user = new Message { ConversationId = conversation.Id, Role = MessageRoles.User, Content = "tolong" };
        var answer = new Message
        {
            ConversationId = conversation.Id, Role = MessageRoles.Assistant, Content = "jawaban", ModelAlias = TestModel,
        };
        db.Messages.AddRange(user, answer);
        await db.SaveChangesAsync();
        return (conversation.Id, user.Id, answer.Id);
    }

    private static async Task<(int Status, string Body)> SaveAsync(AppDbContext db, string userId, long messageId) =>
        await ConversationTestSupport.ExecuteAsync(await CollectionEndpoints.SaveAsync(
            new(messageId), ConversationTestSupport.Request(userId), db, CancellationToken.None));

    private static async Task<(int Status, string Body)> ListAsync(AppDbContext db, string userId) =>
        await ConversationTestSupport.ExecuteAsync(await CollectionEndpoints.ListAsync(
            ConversationTestSupport.Request(userId), db, CancellationToken.None));

    private static async Task<(int Status, string Body)> DeleteAsync(AppDbContext db, string userId, Guid id) =>
        await ConversationTestSupport.ExecuteAsync(await CollectionEndpoints.DeleteAsync(
            id, ConversationTestSupport.Request(userId), db, CancellationToken.None));

    [Fact]
    public async Task Saving_the_same_answer_twice_returns_the_same_row()
    {
        var (_, _, answerId) = await NewAnswerAsync();
        await using var db = NewContext();

        var (firstStatus, firstBody) = await SaveAsync(db, "test", answerId);
        Assert.Equal(201, firstStatus);
        var saved = ConversationTestSupport.Json<SavedAnswerDto>(firstBody);
        Assert.Equal("jawaban", saved.Content);
        Assert.Equal(TestModel, saved.ModelAlias);
        Assert.Equal("judul sesi", saved.SessionTitle);
        Assert.True(saved.SourceAvailable);

        var (secondStatus, secondBody) = await SaveAsync(db, "test", answerId);
        Assert.Equal(200, secondStatus);
        Assert.Equal(saved.Id, ConversationTestSupport.Json<SavedAnswerDto>(secondBody).Id);
        Assert.Single(await db.SavedAnswers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Another_users_answer_is_not_found()
    {
        var (_, _, answerId) = await NewAnswerAsync("user-lain");
        await using var db = NewContext();

        var (status, _) = await SaveAsync(db, "test", answerId);
        Assert.Equal(404, status);
        Assert.Empty(await db.SavedAnswers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Only_assistant_answers_can_be_saved()
    {
        var (_, userMessageId, _) = await NewAnswerAsync();
        await using var db = NewContext();

        var (status, body) = await SaveAsync(db, "test", userMessageId);
        Assert.Equal(400, status);
        Assert.Equal(CollectionEndpoints.NotAnAnswerCode, ConversationTestSupport.CodeOf(body));
        Assert.Empty(await db.SavedAnswers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_saved_answer_removes_it()
    {
        var (_, _, answerId) = await NewAnswerAsync();
        await using var db = NewContext();
        var saved = ConversationTestSupport.Json<SavedAnswerDto>((await SaveAsync(db, "test", answerId)).Body);

        // Milik orang lain: 404 dan barisnya tetap ada, bukan terhapus diam-diam.
        var (foreign, _) = await DeleteAsync(db, "user-lain", saved.Id);
        Assert.Equal(404, foreign);
        Assert.Single(await db.SavedAnswers.AsNoTracking().ToListAsync());

        var (status, _) = await DeleteAsync(db, "test", saved.Id);
        Assert.Equal(204, status);
        Assert.Empty(await db.SavedAnswers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Source_available_turns_false_after_the_session_is_deleted()
    {
        var (conversationId, _, answerId) = await NewAnswerAsync();
        await using (var db = NewContext())
        {
            Assert.Equal(201, (await SaveAsync(db, "test", answerId)).Status);
            var listed = ConversationTestSupport.Json<List<SavedAnswerDto>>((await ListAsync(db, "test")).Body);
            Assert.True(Assert.Single(listed).SourceAvailable);
            // Koleksi user lain tidak pernah ikut terlihat.
            Assert.Empty(ConversationTestSupport.Json<List<SavedAnswerDto>>((await ListAsync(db, "user-lain")).Body));
        }

        await using (var db = NewContext())
        {
            var result = await ConversationDeletion.HandleAsync(conversationId, ConversationTestSupport.Request("test"),
                db, ConversationTestSupport.Storage(_root), NullLoggerFactory.Instance, CancellationToken.None);
            Assert.Equal(204, ((IStatusCodeHttpResult)result).StatusCode);
        }

        await using (var db = NewContext())
        {
            var listed = ConversationTestSupport.Json<List<SavedAnswerDto>>((await ListAsync(db, "test")).Body);
            var saved = Assert.Single(listed);
            Assert.False(saved.SourceAvailable);
            Assert.Equal("jawaban", saved.Content);
        }
    }
}
