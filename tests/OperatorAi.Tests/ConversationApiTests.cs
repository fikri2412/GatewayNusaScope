using Microsoft.EntityFrameworkCore;
using OperatorAi.Api;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

/// <summary>
/// Permukaan yang dijaga API untuk browser: tiket user terkunci ke snapshot kebijakan + model + pesan sumbernya,
/// DTO tidak menyembunyikan potongan jawaban maupun usage yang tidak lengkap, dan penolakan tidak menulis apa pun.
/// </summary>
public class ConversationApiTests : QueueTestBase
{

    [Fact]
    public async Task Each_ticket_keeps_the_policy_and_model_snapshot_it_was_queued_with()
    {
        var conversationId = await NewConversationAsync();
        var first = Policy(maxOutputTokens: 1024);
        var second = Policy(maxOutputTokens: 2048);

        await using (var db = NewContext())
        {
            var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId);
            await ConversationEndpoints.EnqueueUserMessageAsync(db, conversation, "satu", [], "model-a", first, CancellationToken.None);
            await ConversationEndpoints.EnqueueUserMessageAsync(db, conversation, "dua", [], "model-b", second, CancellationToken.None);
        }

        await using var check = NewContext();
        var items = await check.QueueItems.Where(q => q.ConversationId == conversationId).OrderBy(q => q.Id).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.NotEqual(first.PolicyHash, second.PolicyHash);
        Assert.Equal(1024, ExecutionPolicy.FromJson(items[0].ExecutionPolicyJson).MaxOutputTokens);
        Assert.Equal(2048, ExecutionPolicy.FromJson(items[1].ExecutionPolicyJson).MaxOutputTokens);
        // Kiriman kedua memakai modelnya sendiri dan tidak menulis ulang payload tiket pertama.
        Assert.Contains("\"model\":\"model-a\"", items[0].PayloadJson);
        Assert.Contains("\"model\":\"model-b\"", items[1].PayloadJson);
        // Hanya kiriman yang diterima yang mengubah alias percakapan: yang terakhir menang.
        Assert.Equal("model-b", (await check.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId)).ModelAlias);
    }

    [Fact]
    public async Task Sending_in_another_conversation_leaves_this_conversations_snapshot_untouched()
    {
        var a = await NewConversationAsync();
        var b = await NewConversationAsync();

        await using (var db = NewContext())
        {
            var convA = await db.Conversations.SingleAsync(c => c.Id == a);
            var convB = await db.Conversations.SingleAsync(c => c.Id == b);
            await ConversationEndpoints.EnqueueUserMessageAsync(db, convA, "ke A", [], "model-a", Policy(maxOutputTokens: 1024), CancellationToken.None);
            await ConversationEndpoints.EnqueueUserMessageAsync(db, convB, "ke B", [], "model-b", Policy(maxOutputTokens: 2048), CancellationToken.None);
        }

        await using var check = NewContext();
        Assert.Equal("model-a", (await check.Conversations.AsNoTracking().SingleAsync(c => c.Id == a)).ModelAlias);
        Assert.Equal("model-b", (await check.Conversations.AsNoTracking().SingleAsync(c => c.Id == b)).ModelAlias);
        Assert.Equal("model-a", (await check.Messages.AsNoTracking().SingleAsync(m => m.ConversationId == a)).ModelAlias);
        Assert.Equal("model-b", (await check.Messages.AsNoTracking().SingleAsync(m => m.ConversationId == b)).ModelAlias);
        Assert.Contains("\"model\":\"model-a\"", (await check.QueueItems.AsNoTracking().SingleAsync(q => q.ConversationId == a)).PayloadJson);
        Assert.Contains("\"model\":\"model-b\"", (await check.QueueItems.AsNoTracking().SingleAsync(q => q.ConversationId == b)).PayloadJson);
    }

    [Fact]
    public async Task Rejected_alias_or_foreign_conversation_writes_nothing()
    {
        var conversationId = await NewConversationAsync();
        IReadOnlyList<ModelInfoDto> catalog =
        [
            new("model-a", true, false, true, 1024, null),
            new("model-b", false, null, true, null, null),                 // tool tidak didukung
            new("model-c", null, null, false, null, "readiness_unknown"), // route belum siap
        ];

        await using var db = NewContext();
        var listCalls = 0;
        Task<IReadOnlyList<ModelInfoDto>> List(CancellationToken _)
        {
            listCalls++;
            return Task.FromResult(catalog);
        }

        // Percakapan milik user lain: ditolak 404 dan katalog bahkan tidak pernah diminta.
        var foreign = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "user-lain", conversationId,
            new("halo", null, "model-a"), List, Policy(), CancellationToken.None);
        Assert.Equal(404, foreign.Status);
        Assert.Equal(0, listCalls);

        // Alias di luar katalog, tools belum dikonfirmasi, route belum siap, dan alias kosong: semuanya tanpa baris baru.
        foreach (var alias in new[] { "tidak-ada", "model-b", "model-c" })
        {
            var rejected = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
                new("halo", null, alias), List, Policy(), CancellationToken.None);
            Assert.Equal(400, rejected.Status);
        }
        var missing = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
            new("halo", null, " "), List, Policy(), CancellationToken.None);
        Assert.Equal(400, missing.Status);

        Assert.Equal(3, listCalls); // hanya alias yang valid bentuknya sampai ke katalog
        Assert.Empty(await db.Messages.ToListAsync());
        Assert.Empty(await db.QueueItems.ToListAsync());
        Assert.Null((await db.Conversations.SingleAsync(c => c.Id == conversationId)).ModelAlias);
    }

    [Fact]
    public async Task Auto_model_selects_vision_model_when_images_are_attached_and_general_model_otherwise()
    {
        var conversationId = await NewConversationAsync();
        IReadOnlyList<ModelInfoDto> catalog =
        [
            new("deepseek-v4.1-flash", true, false, true, 4096, null),
            new("glm-5.3-flash", true, true, true, 4096, null, SupportsVision: true),
        ];
        Task<IReadOnlyList<ModelInfoDto>> List(CancellationToken _) => Task.FromResult(catalog);

        await using var db = NewContext();

        // 1. Teks biasa dengan model "auto" -> memilih deepseek, pesan tercatat deepseek, conversation tercatat auto.
        var textOnly = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
            new("Pertanyaan teks", null, "auto"), List, Policy(), CancellationToken.None);
        Assert.Equal(202, textOnly.Status);
        Assert.Equal("deepseek-v4.1-flash", textOnly.Message!.ModelAlias);

        var item1 = await db.QueueItems.SingleAsync(q => q.Id == textOnly.QueueItemId);
        Assert.Contains("\"model\":\"deepseek-v4.1-flash\"", item1.PayloadJson);
        Assert.Equal("auto", (await db.Conversations.SingleAsync(c => c.Id == conversationId)).ModelAlias);

        // 2. Lampirkan gambar PNG -> model "auto" memilih model vision (glm-5.3-flash).
        var imageId = Guid.NewGuid();
        db.Attachments.Add(new Attachment
        {
            Id = imageId, ConversationId = conversationId, FileName = "foto.png",
            StoragePath = "uploads/foto.png", Kind = AttachmentKinds.Upload, ContentType = "image/png", SizeBytes = 100,
        });
        await db.SaveChangesAsync();

        var withImage = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
            new("Lihat gambar ini", [imageId], "auto"), List, Policy(), CancellationToken.None);
        Assert.Equal(202, withImage.Status);
        Assert.Equal("glm-5.3-flash", withImage.Message!.ModelAlias);

        var item2 = await db.QueueItems.SingleAsync(q => q.Id == withImage.QueueItemId);
        Assert.Contains("\"model\":\"glm-5.3-flash\"", item2.PayloadJson);
        Assert.Contains("\"supportsVision\":true", item2.PayloadJson);
        Assert.Equal("auto", (await db.Conversations.SingleAsync(c => c.Id == conversationId)).ModelAlias);

        // 3. Bila tidak ada model vision yang siap, "auto" dengan gambar ditolak 400.
        IReadOnlyList<ModelInfoDto> textOnlyCatalog = [new("deepseek-v4.1-flash", true, false, true, 4096, null)];
        var rejectedVision = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
            new("Gambar tanpa model vision", [imageId], "auto"), _ => Task.FromResult(textOnlyCatalog), Policy(), CancellationToken.None);
        Assert.Equal(400, rejectedVision.Status);
        Assert.Contains("vision", rejectedVision.Error);
    }

    [Theory]
    [InlineData("deep", "high")]
    [InlineData("fast", "low")]
    [InlineData(null, null)]
    public async Task ThinkMode_maps_to_reasoning_effort_in_payload_json(string? thinkMode, string? expectedEffort)
    {
        var conversationId = await NewConversationAsync();
        IReadOnlyList<ModelInfoDto> catalog = [new("deepseek-v4.1-flash", true, false, true, 4096, null)];
        Task<IReadOnlyList<ModelInfoDto>> List(CancellationToken _) => Task.FromResult(catalog);

        await using var db = NewContext();
        var result = await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
            new("Pertanyaan", null, "deepseek-v4.1-flash", thinkMode), List, Policy(), CancellationToken.None);
        Assert.Equal(202, result.Status);

        var item = await db.QueueItems.SingleAsync(q => q.Id == result.QueueItemId);
        var effort = ModelSnapshot.ReasoningEffort(item.PayloadJson);
        Assert.Equal(expectedEffort, effort);
    }

    [Fact]
    public async Task Dto_keeps_finish_reason_and_reports_usage_as_lower_bound()
    {
        var conversationId = await NewConversationAsync();
        await using (var db = NewContext())
        {
            db.Messages.AddRange(
                new Message { ConversationId = conversationId, Role = MessageRoles.Assistant, Content = "sebagian…", FinishReason = "length" },
                new Message { ConversationId = conversationId, Role = MessageRoles.Assistant, Content = "utuh" });
            var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId);
            conversation.TokensUsed = 1234;
            conversation.UsageIncomplete = true;
            await db.SaveChangesAsync();
        }

        await using var check = NewContext();
        var rows = await check.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Id).ToListAsync();
        var dtos = await check.ToDtosAsync(rows, CancellationToken.None);

        Assert.Equal("length", dtos[0].FinishReason);
        Assert.Null(dtos[1].FinishReason);

        var conversationDto = (await check.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId)).ToDto();
        Assert.Equal(1234, conversationDto.TokensUsed);
        Assert.True(conversationDto.UsageIncomplete);
    }
}
