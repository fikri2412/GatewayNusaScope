using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;
using OperatorAi.Worker.Specialist.Handlers;

namespace OperatorAi.Tests;

/// <summary>
/// Job ExcelRead (DESIGN bagian 11.1): input dan lampiran divalidasi, file diambil dari FileStorage, dan
/// lampiran percakapan lain tidak boleh terbaca (job hanya melihat percakapan miliknya).
/// </summary>
public class ExcelReadHandlerTests : QueueTestBase
{
    private static string SamplesDir() => Path.Combine(AppContext.BaseDirectory, "samples");

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "OperatorAi_Excel_Test", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Job aktif milik worker: snapshot kebijakan + pesan sumber yang sudah divalidasi (aturan ck_jobs_execution_policy),
    /// bukan baris job tanpa asal-usul.
    /// </summary>
    private async Task<JobContext> NewJobContextAsync(Guid conversationId, string inputJson, ExecutionPolicy? policy = null)
    {
        await using var db = NewContext();
        var source = new Message { ConversationId = conversationId, Role = MessageRoles.User, Content = "analisis file ini", ModelAlias = TestModel };
        db.Messages.Add(source);
        await db.SaveChangesAsync();
        policy ??= Policy();
        var input = JsonNode.Parse(inputJson)!.AsObject();
        input["model"] ??= TestModel;
        var job = new Job
        {
            ConversationId = conversationId, JobType = JobTypes.ExcelRead, InputJson = input.ToJsonString(),
            ExecutionPolicyJson = policy.ToJson(), SourceMessageId = source.Id,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return new JobContext(job, null, policy, source.Id, TestModel);
    }

    private async Task<InvalidOperationException> RejectAsync(Guid conversationId, string inputJson)
    {
        var handler = new ExcelReadHandler(DbOptions, new FileStorage(Options.Create(new StorageOptions { RootPath = NewTempRoot() })));
        var context = await NewJobContextAsync(conversationId, inputJson);
        return await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Profiles_an_upload_of_the_same_conversation()
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await NewAttachmentAsync(conversation, "data.csv");
        var root = NewTempRoot();
        try
        {
            var storage = new FileStorage(Options.Create(new StorageOptions { RootPath = root }));
            await using (var sample = File.OpenRead(Path.Combine(SamplesDir(), "pemisah-titik-koma.csv")))
                await storage.SaveAsync("uploads/data.csv", sample, CancellationToken.None);
            var handler = new ExcelReadHandler(DbOptions, storage);
            var context = await NewJobContextAsync(conversation, $"{{\"attachmentId\":\"{attachmentId}\"}}");

            var output = await handler.HandleAsync(context, CancellationToken.None);

            using var document = JsonDocument.Parse(output);
            var sheet = document.RootElement.GetProperty("sheets")[0];
            Assert.Equal(4, sheet.GetProperty("rowCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reduced_upload_policy_rejects_previously_uploaded_spreadsheets(bool extensionDisabled)
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await NewAttachmentAsync(conversation, "data.csv");
        var root = NewTempRoot();
        try
        {
            var storage = new FileStorage(Options.Create(new StorageOptions { RootPath = root }));
            await using (var sample = File.OpenRead(Path.Combine(SamplesDir(), "pemisah-titik-koma.csv")))
                await storage.SaveAsync("uploads/data.csv", sample, CancellationToken.None);
            var upload = extensionDisabled
                ? UploadRuntimeLimits.Default with { AllowedExtensions = [".xlsx"] }
                : UploadRuntimeLimits.Default with { MaxBytes = 32 };
            var context = await NewJobContextAsync(conversation, $"{{\"attachmentId\":\"{attachmentId}\"}}", Policy(upload: upload));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ExcelReadHandler(DbOptions, storage).HandleAsync(context, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Rejects_an_attachment_from_another_conversation()
    {
        var mine = await NewConversationAsync();
        var theirs = await NewConversationAsync();
        var foreign = await NewAttachmentAsync(theirs);

        await RejectAsync(mine, $"{{\"attachmentId\":\"{foreign}\"}}");
    }

    [Theory]
    [InlineData("data.pdf", AttachmentKinds.Upload)]
    [InlineData("data.csv", AttachmentKinds.Output)]
    public async Task Rejects_wrong_extension_or_kind(string fileName, string kind)
    {
        var conversation = await NewConversationAsync();
        var attachmentId = await NewAttachmentAsync(conversation, fileName, kind);

        await RejectAsync(conversation, $"{{\"attachmentId\":\"{attachmentId}\"}}");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"attachmentId":"bukan-guid"}""")]
    [InlineData("""{"attachmentId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task Rejects_invalid_input(string inputJson)
    {
        var conversation = await NewConversationAsync();

        await RejectAsync(conversation, inputJson);
    }
}
