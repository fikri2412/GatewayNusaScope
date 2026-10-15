using OperatorAi.Api;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

/// <summary>
/// Riwayat pesan (§4.2) yang dilihat browser: pesan sumber hanya terisi untuk balasan, flag alternatif ikut,
/// dan baris lama tanpa timing tetap null — bukan angka nol yang menyesatkan.
/// </summary>
public class ConversationHistoryTests : QueueTestBase
{
    [Fact]
    public async Task History_fills_source_flags_and_timing_from_the_row()
    {
        var conversationId = await NewConversationAsync();
        await using (var seed = NewContext())
        {
            var question = new Message { ConversationId = conversationId, Role = MessageRoles.User, Content = "halo" };
            seed.Messages.Add(question);
            await seed.SaveChangesAsync();
            var item = new QueueItem
            {
                ConversationId = conversationId, Type = QueueTypes.UserMessage, PayloadJson = PacketJson(),
                ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = question.Id, Status = Statuses.Done,
            };
            seed.QueueItems.Add(item);
            await seed.SaveChangesAsync(); // id tiket dipakai balasan dan catatan tool di bawah
            seed.Messages.Add(new Message
            {
                ConversationId = conversationId, Role = MessageRoles.Assistant, Content = "jawaban", ModelAlias = TestModel,
                QueueItemId = item.Id, IsPrimary = false, TokensUsed = 120, QueueWaitMs = 30, FirstTokenMs = 40,
                TotalMs = 900,
            });
            // Pesan tersembunyi (catatan tool) tidak pernah ikut ke riwayat.
            seed.Messages.Add(new Message
            {
                ConversationId = conversationId, Role = MessageRoles.User, Content = "tool", IsVisible = false,
                QueueItemId = item.Id,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var (status, body) = await ConversationTestSupport.ExecuteAsync(await ConversationEndpoints.MessagesAsync(
            conversationId, ConversationTestSupport.Request("test"), db, CancellationToken.None));
        Assert.Equal(200, status);

        var rows = ConversationTestSupport.Json<List<MessageDto>>(body);
        Assert.Equal(2, rows.Count);

        var user = rows[0];
        Assert.Equal(MessageRoles.User, user.Role);
        Assert.Null(user.SourceMessageId);
        Assert.True(user.IsPrimary);
        Assert.Null(user.TokensUsed);
        Assert.Null(user.QueueWaitMs);
        Assert.Null(user.FirstTokenMs);
        Assert.Null(user.TotalMs);

        var answer = rows[1];
        Assert.Equal(MessageRoles.Assistant, answer.Role);
        Assert.Equal(user.Id, answer.SourceMessageId);
        Assert.Equal(TestModel, answer.ModelAlias);
        Assert.False(answer.IsPrimary);
        Assert.Equal(120, answer.TokensUsed);
        Assert.Equal(30, answer.QueueWaitMs);
        Assert.Equal(40, answer.FirstTokenMs);
        Assert.Equal(900, answer.TotalMs);

        // Kepemilikan tetap: riwayat percakapan user lain tidak pernah terlihat.
        var foreign = await ConversationTestSupport.ExecuteAsync(await ConversationEndpoints.MessagesAsync(
            conversationId, ConversationTestSupport.Request("user-lain"), db, CancellationToken.None));
        Assert.Equal(404, foreign.Status);
    }
}
