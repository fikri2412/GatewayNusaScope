using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

/// <summary>
/// Basis test dokumen 6C: storage sementara per test, lampiran upload nyata di disk, dan baris Job/JobContext
/// seperti yang dibuat worker (snapshot kebijakan + pesan sumber).
/// </summary>
public abstract class DocumentTestBase : QueueTestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OperatorAi_Doc_Test", Guid.NewGuid().ToString("N"));

    protected FileStorage Storage => new(Options.Create(new StorageOptions { RootPath = _root }));

    protected string StorageRoot => _root;

    protected static string RelativePath(Guid conversationId, Guid attachmentId, string fileName) =>
        FileStorage.UploadPath(conversationId, attachmentId, Path.GetExtension(fileName));

    protected async Task<Guid> UploadAsync(Guid conversationId, string fileName, byte[] content)
    {
        var id = Guid.NewGuid();
        var relative = FileStorage.UploadPath(conversationId, id, Path.GetExtension(fileName));
        await using (var stream = new MemoryStream(content))
            await Storage.SaveAsync(relative, stream, CancellationToken.None);
        await using var db = NewContext();
        db.Attachments.Add(new Attachment
        {
            Id = id, ConversationId = conversationId, FileName = fileName, StoragePath = relative,
            Kind = AttachmentKinds.Upload, ContentType = "application/octet-stream", SizeBytes = content.Length,
        });
        await db.SaveChangesAsync();
        return id;
    }

    protected async Task<JobContext> NewJobContextAsync(Guid conversationId, string jobType, string inputJson,
        string? dependencyOutput = null, long? dependsOnJobId = null, ExecutionPolicy? policy = null, string? goal = null,
        bool rawInput = false)
    {
        await using var db = NewContext();
        var source = new Message
        {
            ConversationId = conversationId, Role = MessageRoles.User, ModelAlias = TestModel,
            Content = goal ?? "Tolong analisis dokumen ini dan jelaskan isinya.",
        };
        db.Messages.Add(source);
        await db.SaveChangesAsync();
        // rawInput menyimpan teks mentah (untuk menguji input yang memang tidak bisa diparse).
        var snapshot = rawInput ? new JsonObject { ["raw"] = inputJson } : JsonNode.Parse(inputJson)!.AsObject();
        snapshot["model"] ??= TestModel;
        var job = new Job
        {
            ConversationId = conversationId, JobType = jobType, InputJson = snapshot.ToJsonString(),
            ExecutionPolicyJson = (policy ?? Policy()).ToJson(), SourceMessageId = source.Id, DependsOnJobId = dependsOnJobId,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return new JobContext(job, dependencyOutput, policy ?? Policy(), source.Id, TestModel);
    }

    /// <summary>Kontrak tool: attachment_id + fokus + (opsional) pemeriksaan visual semua halaman + snapshot vision.</summary>
    protected static string ReadInput(Guid attachmentId, bool visual = false, bool supportsVision = true) =>
        System.Text.Json.JsonSerializer.Serialize(new { attachmentId, visual, supportsVision });

    protected static string AnalyzeInput(string tujuan = "Fokus tool", bool supportsVision = true, bool? langsung = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { tujuan, supportsVision, langsung });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
