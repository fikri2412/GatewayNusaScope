using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Api;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

/// <summary>
/// Hapus sesi (§4.1) dari sisi pemakai: sesi user lain 404, sesi yang masih bekerja 409, dan setelah 204 tidak ada
/// baris, lampiran, atau file yang tersisa — kecuali snapshot jawaban koleksi yang referensinya dilepas.
/// </summary>
public class ConversationDeleteTests : QueueTestBase, IDisposable
{
    private readonly string _root = ConversationTestSupport.NewTempRoot();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<(int Status, string Body)> DeleteAsync(Guid conversationId, string userId)
    {
        await using var db = NewContext();
        var result = await ConversationDeletion.HandleAsync(conversationId, ConversationTestSupport.Request(userId), db,
            ConversationTestSupport.Storage(_root), NullLoggerFactory.Instance, CancellationToken.None);
        return await ConversationTestSupport.ExecuteAsync(result);
    }

    /// <summary>Sesi lengkap seperti setelah dipakai: pesan user, tiket selesai, job gagal, lampiran berfile, jawaban koleksi.</summary>
    private async Task<(Guid ConversationId, string StoragePath)> NewSessionAsync()
    {
        var conversationId = await NewConversationAsync();
        await using var db = NewContext();
        var message = new Message { ConversationId = conversationId, Role = MessageRoles.User, Content = "halo" };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        db.QueueItems.Add(new QueueItem
        {
            ConversationId = conversationId, Type = QueueTypes.UserMessage, PayloadJson = PacketJson(),
            ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = message.Id, Status = Statuses.Done,
        });
        db.Jobs.Add(new Job
        {
            ConversationId = conversationId, JobType = JobTypes.ExcelRead, InputJson = PacketJson(),
            ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = message.Id, Status = Statuses.Failed,
        });
        var attachment = new Attachment
        {
            ConversationId = conversationId, FileName = "data.xlsx", Kind = AttachmentKinds.Upload,
            StoragePath = $"uploads/{conversationId}/{Guid.NewGuid():N}.xlsx", ContentType = "application/octet-stream",
            SizeBytes = 4,
        };
        db.Attachments.Add(attachment);
        db.SavedAnswers.Add(new SavedAnswer
        {
            UserId = "test", ConversationId = conversationId, MessageId = message.Id, SessionTitle = "judul",
            ModelAlias = TestModel, Content = "jawaban tersimpan", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var full = FullPath(_root, attachment.StoragePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "xlsx");
        return (conversationId, attachment.StoragePath);
    }

    private static string FullPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Another_users_session_is_not_found_and_stays()
    {
        var (conversationId, storagePath) = await NewSessionAsync();

        var (status, _) = await DeleteAsync(conversationId, "user-lain");
        Assert.Equal(404, status);

        await using var db = NewContext();
        Assert.Single(await db.Conversations.Where(c => c.Id == conversationId).ToListAsync());
        Assert.Single(await db.Messages.Where(m => m.ConversationId == conversationId).ToListAsync());
        Assert.True(File.Exists(FullPath(_root, storagePath)));
    }

    [Fact]
    public async Task A_session_with_work_in_flight_is_busy()
    {
        var conversationId = await NewConversationAsync();
        await EnqueueAsync(conversationId); // Pending
        var (pendingTicket, pendingBody) = await DeleteAsync(conversationId, "test");
        Assert.Equal(409, pendingTicket);
        Assert.Equal(ConversationDeletion.BusyCode, ConversationTestSupport.CodeOf(pendingBody));

        // Sesi 409 tidak kehilangan apa pun.
        await using var check = NewContext();
        Assert.Single(await check.Conversations.Where(c => c.Id == conversationId).ToListAsync());
        Assert.Single(await check.QueueItems.Where(q => q.ConversationId == conversationId).ToListAsync());

        // Job yang belum selesai juga menahan hapus, walaupun tidak ada tiket antrean yang berjalan.
        var jobSession = await NewConversationAsync();
        await using (var jobs = NewContext())
        {
            jobs.Jobs.Add(new Job
            {
                ConversationId = jobSession, JobType = JobTypes.Analyze, InputJson = PacketJson(),
                ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = 1, Status = Statuses.Processing,
            });
            await jobs.SaveChangesAsync();
        }
        Assert.Equal(409, (await DeleteAsync(jobSession, "test")).Status);
    }

    [Fact]
    public async Task Deleting_a_session_removes_its_rows_and_files()
    {
        var (conversationId, storagePath) = await NewSessionAsync();
        var file = FullPath(_root, storagePath);
        var directory = Path.GetDirectoryName(file)!;
        Assert.True(File.Exists(file));

        var (status, body) = await DeleteAsync(conversationId, "test");
        Assert.Equal(204, status);
        Assert.Equal("", body);

        await using var db = NewContext();
        Assert.Empty(await db.Conversations.Where(c => c.Id == conversationId).ToListAsync());
        Assert.Empty(await db.Messages.Where(m => m.ConversationId == conversationId).ToListAsync());
        Assert.Empty(await db.QueueItems.Where(q => q.ConversationId == conversationId).ToListAsync());
        Assert.Empty(await db.Jobs.Where(j => j.ConversationId == conversationId).ToListAsync());
        Assert.Empty(await db.Attachments.Where(a => a.ConversationId == conversationId).ToListAsync());
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Saved_answer_keeps_its_snapshot_and_loses_the_reference()
    {
        var (conversationId, _) = await NewSessionAsync();

        Assert.Equal(204, (await DeleteAsync(conversationId, "test")).Status);

        await using var db = NewContext();
        var saved = await db.SavedAnswers.SingleAsync(s => s.UserId == "test");
        Assert.Equal("jawaban tersimpan", saved.Content);
        Assert.Equal("judul", saved.SessionTitle);
        Assert.Null(saved.ConversationId);
        Assert.Null(saved.MessageId);
    }
}
