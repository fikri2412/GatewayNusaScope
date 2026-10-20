using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

/// <summary>
/// Database LocalDB sementara per test (xUnit membuat satu instance kelas per test), dibuat lewat migration
/// sungguhan lalu dihapus. Klaim queue global ke tabel, jadi tiap test butuh tabel kosong sendiri.
/// </summary>
public abstract class QueueTestBase : IAsyncLifetime
{
    private const string Server = "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";
    private readonly string _database = $"OperatorAi_Test_{Guid.NewGuid():N}";

    /// <summary>Alias model yang dipakai semua fixture aktif, seperti yang ditulis API (payload tiket) dan worker (input job).</summary>
    protected const string TestModel = "deepseek-v4.1-flash";

    protected string ConnectionString => $"{Server};Database={_database}";

    protected DbContextOptions<AppDbContext> DbOptions =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options;

    protected AppDbContext NewContext() => new(DbOptions);

    protected async Task<Guid> NewAttachmentAsync(Guid conversationId, string fileName = "data.xlsx", string kind = AttachmentKinds.Upload)
    {
        await using var db = NewContext();
        var a = new Attachment { ConversationId = conversationId, FileName = fileName, StoragePath = $"uploads/{fileName}", Kind = kind, ContentType = "application/octet-stream" };
        db.Attachments.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    /// <summary>Pesan user + tiket <c>UserMessage</c> seperti yang ditulis <c>POST /messages</c>.</summary>
    protected async Task<(long MessageId, long ItemId)> NewUserTicketAsync(Guid conversationId, string text,
        params Guid[] attachmentIds) => await NewUserTicketAsync(conversationId, text, TestModel, attachmentIds);

    /// <param name="model">Model workflow yang dipilih user untuk pesan ini; ikut ke payload tiket, bukan dari percakapan.</param>
    protected async Task<(long MessageId, long ItemId)> NewUserTicketAsync(Guid conversationId, string text, string model,
        params Guid[] attachmentIds)
    {
        await using var db = NewContext();
        var message = new Message
        {
            ConversationId = conversationId, Role = MessageRoles.User, Content = text, ModelAlias = model,
            AttachmentIdsJson = attachmentIds.Length == 0 ? null : System.Text.Json.JsonSerializer.Serialize(attachmentIds),
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        var item = new QueueItem
        {
            ConversationId = conversationId, Type = QueueTypes.UserMessage,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { messageId = message.Id, model }),
            ExecutionPolicyJson = Policy().ToJson(), SourceMessageId = message.Id,
        };
        db.QueueItems.Add(item);
        await db.SaveChangesAsync();
        return (message.Id, item.Id);
    }

    protected QueueRepository NewRepo() => new(ConnectionString);

    protected static ExecutionPolicy Policy(int maxToolLoops = 5, int maxHistoryMessages = 40,
        int maxTokensPerConversation = 200000, int maxRetries = 3, int staleLockMinutes = 10,
        int maxOutputTokens = 4096, string detail = "mendalam", string additionalInstructions = "",
        UploadRuntimeLimits? upload = null, DocumentRuntimeLimits? document = null, string fileTools = FileToolModes.Auto) =>
        ExecutionPolicy.Create(
            new OperatorOptions { MaxToolLoops = maxToolLoops, MaxHistoryMessages = maxHistoryMessages, MaxTokensPerConversation = maxTokensPerConversation, FileTools = fileTools },
            new QueueOptions { MaxRetries = maxRetries, StaleLockMinutes = staleLockMinutes },
            new AiOptions { MaxTokens = maxOutputTokens },
            new ResponseOptions { Detail = detail }, new PromptOptions { AdditionalInstructions = additionalInstructions },
            upload: upload, document: document);

    protected async Task SetQueuePolicyAsync(long itemId, ExecutionPolicy policy)
    {
        await using var db = NewContext();
        await db.QueueItems.Where(q => q.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(q => q.ExecutionPolicyJson, policy.ToJson()));
    }

    /// <summary>
    /// LocalDB membuka koneksi fisik baru dengan lambat (~0,8 dtk, berurutan). Tanpa pemanasan, satu pengklaim
    /// menghabiskan seluruh antrean sebelum yang lain sempat tersambung dan test "paralel" tidak menguji
    /// persaingan sama sekali. Buka <paramref name="count"/> koneksi sekaligus lalu kembalikan ke pool.
    /// </summary>
    protected async Task WarmPoolAsync(int count)
    {
        var connections = await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
        {
            var c = new SqlConnection(ConnectionString);
            await c.OpenAsync();
            return c;
        }));
        foreach (var c in connections) await c.DisposeAsync();
    }

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        using (var own = new SqlConnection(ConnectionString)) SqlConnection.ClearPool(own);
        await using var master = new SqlConnection(Server);
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];";
        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task<Guid> NewConversationAsync()
    {
        await using var db = NewContext();
        var c = new Conversation { UserId = "test" };
        db.Conversations.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    protected async Task<long> EnqueueAsync(Guid conversationId, string type = QueueTypes.UserMessage, ExecutionPolicy? policy = null)
    {
        await using var db = NewContext();
        var item = new QueueItem { ConversationId = conversationId, Type = type, PayloadJson = PacketJson(),
            ExecutionPolicyJson = (policy ?? Policy()).ToJson(), SourceMessageId = 1 };
        db.QueueItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    protected async Task<long> NewJobAsync(Guid conversationId, long? dependsOn = null, bool notifyOperator = false,
        string jobType = JobTypes.ExcelRead, ExecutionPolicy? policy = null)
    {
        await using var db = NewContext();
        var job = new Job
        {
            ConversationId = conversationId, JobType = jobType, InputJson = PacketJson(),
            DependsOnJobId = dependsOn, NotifyOperator = notifyOperator,
            ExecutionPolicyJson = (policy ?? Policy()).ToJson(), SourceMessageId = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    /// <summary>Snapshot tiket/job minimal yang sah untuk baris aktif: hanya model workflow.</summary>
    protected static string PacketJson(string model = TestModel) => System.Text.Json.JsonSerializer.Serialize(new { model });

    protected async Task<QueueItem> GetItemAsync(long id)
    {
        await using var db = NewContext();
        return await db.QueueItems.AsNoTracking().SingleAsync(q => q.Id == id);
    }

    protected async Task<Job> GetJobAsync(long id)
    {
        await using var db = NewContext();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    protected async Task AgeLockAsync(long queueItemId, int minutes)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE queue_items SET locked_at = DATEADD(MINUTE, -{0}, SYSUTCDATETIME()) WHERE id = {1}", minutes, queueItemId);
    }
}
