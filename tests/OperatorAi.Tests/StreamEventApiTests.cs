using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OperatorAi.Api;
using OperatorAi.Api.Hubs;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Tests;

/// <summary>
/// Jalur streaming worker → browser: batch hanya diteruskan bila tiketnya Processing dan masih dikunci run pengirim,
/// dan snapshot activity memberi tahu UI kapan pekerjaan benar-benar selesai (putaran tool vs balasan terakhir),
/// termasuk saat balasan assistant disembunyikan.
/// </summary>
public class StreamEventApiTests : QueueTestBase
{
    private const string Key = "0123456789abcdef0123456789abcdef", RunId = "run-1";

    [Fact]
    public void Internal_key_must_match_exactly()
    {
        Assert.True(InternalEndpoints.KeyMatches(Key, Key));
        Assert.False(InternalEndpoints.KeyMatches(null, Key));
        Assert.False(InternalEndpoints.KeyMatches("", Key));
        Assert.False(InternalEndpoints.KeyMatches("salah", Key));
        Assert.False(InternalEndpoints.KeyMatches(Key + "x", Key));   // prefix benar tapi beda panjang
        Assert.False(InternalEndpoints.KeyMatches(Key[..^1], Key));
    }

    [Fact]
    public async Task Valid_batch_is_forwarded_to_the_conversation_group_without_touching_the_ticket()
    {
        var conversationId = await NewConversationAsync();
        var itemId = await ClaimedTicketAsync(conversationId);
        var (hub, notifier) = NewNotifier();
        await using var db = NewContext();

        var delta = Batch(conversationId, itemId, sequence: 1, offset: 0, text: "Hai");
        var started = Batch(conversationId, itemId, sequence: 0, offset: 0, text: null, kind: StreamEventKinds.Started);
        var aborted = Batch(conversationId, itemId, sequence: 2, offset: 3, text: null, kind: StreamEventKinds.Aborted);
        foreach (var batch in new[] { started, delta, aborted })
            Assert.Equal(204, (await InternalEndpoints.ForwardStreamEventAsync(db, notifier, batch, CancellationToken.None)).Status);

        Assert.Equal(3, hub.Sent.Count);
        Assert.All(hub.Sent, s => Assert.Equal(ChatNotifier.Group(conversationId), s.Group));
        Assert.All(hub.Sent, s => Assert.Equal(NotifyTypes.StreamEvent, s.Method));
        Assert.Equal(delta, hub.Sent[1].Payload);

        // Batch draft tidak menulis apa pun: tiket tetap milik run pengirim.
        var item = await GetItemAsync(itemId);
        Assert.Equal(Statuses.Processing, item.Status);
        Assert.Equal(RunId, item.LockedBy);
    }

    [Fact]
    public async Task Batch_from_another_run_conversation_or_unclaimed_ticket_is_never_forwarded()
    {
        var conversationId = await NewConversationAsync();
        var itemId = await ClaimedTicketAsync(conversationId);
        var other = await NewConversationAsync();
        var (_, pendingId) = await NewUserTicketAsync(other, "halo");
        var (hub, notifier) = NewNotifier();
        await using var db = NewContext();

        // Worker lama yang lease-nya sudah diambil proses lain, percakapan lain, dan tiket Pending (belum diklaim)
        // semuanya ditolak tanpa satu pun event ke browser.
        var wrongRun = await InternalEndpoints.ForwardStreamEventAsync(db, notifier,
            Batch(conversationId, itemId, sequence: 1, offset: 0, text: "x", runId: "run-lama"), CancellationToken.None);
        var wrongConversation = await InternalEndpoints.ForwardStreamEventAsync(db, notifier,
            Batch(Guid.NewGuid(), itemId, sequence: 1, offset: 0, text: "x"), CancellationToken.None);
        var pending = await InternalEndpoints.ForwardStreamEventAsync(db, notifier,
            Batch(other, pendingId, sequence: 1, offset: 0, text: "x"), CancellationToken.None);

        Assert.Equal(409, wrongRun.Status);
        Assert.Equal(409, wrongConversation.Status);
        Assert.Equal(409, pending.Status);
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task Malformed_batch_is_rejected_without_forwarding()
    {
        var conversationId = await NewConversationAsync();
        var itemId = await ClaimedTicketAsync(conversationId);
        var (hub, notifier) = NewNotifier();
        await using var db = NewContext();

        var malformed = new[]
        {
            Batch(conversationId, itemId, 1, 0, "x", kind: "selesai"),                                   // kind tidak dikenal
            Batch(conversationId, itemId, 1, 0, null),                                                   // delta tanpa teks
            Batch(conversationId, itemId, 1, 0, ""),                                                     // delta teks kosong
            Batch(conversationId, itemId, 1, 0, "x", kind: StreamEventKinds.Started),                    // started wajib sequence 0
            Batch(conversationId, itemId, 0, 0, "x", kind: StreamEventKinds.Started),                    // started dengan teks
            Batch(conversationId, itemId, 0, 0, null, kind: StreamEventKinds.Aborted),                   // aborted wajib sequence lanjutan
            Batch(conversationId, itemId, 2, 3, "x", kind: StreamEventKinds.Aborted),                    // aborted tanpa teks
            Batch(conversationId, itemId, -1, 0, "x"),                                                   // sequence negatif
            Batch(conversationId, itemId, 1, -1, "x"),                                                   // offset negatif
            Batch(conversationId, itemId, 1, 0, new string('x', 32 * 1024 + 1)),                         // text melewati batas batch
            Batch(conversationId, itemId, 1, 0, "x", runId: new string('r', 51)),                        // runId melebihi locked_by
            Batch(Guid.Empty, itemId, 1, 0, "x"),                                                        // conversationId kosong
            Batch(conversationId, itemId, 1, 0, "x", streamId: Guid.Empty),                              // streamId kosong
            Batch(conversationId, itemId, 1, 0, "x", round: -1),                                         // round negatif
            Batch(conversationId, 0, 1, 0, "x"),                                                         // queueItemId kosong
        };

        foreach (var batch in malformed)
            Assert.Equal(400, (await InternalEndpoints.ForwardStreamEventAsync(db, notifier, batch, CancellationToken.None)).Status);
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task Activity_snapshot_stays_while_ticket_runs_and_clears_when_terminal()
    {
        var conversationId = await NewConversationAsync();
        var itemId = await ClaimedTicketAsync(conversationId);
        var (hub, notifier) = NewNotifier();

        // Balasan putaran tool: baris assistant sudah canonical tapi tiket masih Processing → UI tetap menunggu.
        var roundId = await AddMessageAsync(conversationId, "sebentar…", visible: true, itemId);
        Assert.True(await notifier.PublishMessageAsync(conversationId, roundId, CancellationToken.None));
        Assert.Equal([NotifyTypes.MessageCreated, NotifyTypes.ActivityChanged], hub.Sent.Select(s => s.Method));
        Assert.Equal(await ActivityAsync(itemId, Statuses.Processing), Assert.Single(Assert.IsType<List<ActivityDto>>(hub.Sent[^1].Payload)));

        // Balasan tersembunyi tidak pernah dipalsukan jadi pesan, tetapi snapshot tetap dikirim.
        var hiddenId = await AddMessageAsync(conversationId, "tool", visible: false, itemId);
        hub.Sent.Clear();
        Assert.True(await notifier.PublishMessageAsync(conversationId, hiddenId, CancellationToken.None));
        Assert.Equal([NotifyTypes.ActivityChanged], hub.Sent.Select(s => s.Method));
        Assert.Equal(await ActivityAsync(itemId, Statuses.Processing), Assert.Single(Assert.IsType<List<ActivityDto>>(hub.Sent[0].Payload)));

        // Tiket selesai: snapshot kosong supaya UI berhenti menunggu.
        await using (var db = NewContext())
            await db.QueueItems.Where(q => q.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, Statuses.Done));
        var finalId = await AddMessageAsync(conversationId, "jawaban akhir", visible: true, itemId);
        hub.Sent.Clear();
        Assert.True(await notifier.PublishMessageAsync(conversationId, finalId, CancellationToken.None));
        Assert.Equal([NotifyTypes.MessageCreated, NotifyTypes.ActivityChanged], hub.Sent.Select(s => s.Method));
        Assert.Empty(Assert.IsType<List<ActivityDto>>(hub.Sent[^1].Payload));

        // Pesan percakapan lain tidak pernah bocor ke group ini.
        hub.Sent.Clear();
        Assert.False(await notifier.PublishMessageAsync(Guid.NewGuid(), finalId, CancellationToken.None));
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task Worker_activity_notify_publishes_snapshot_only_for_its_own_ticket()
    {
        var conversationId = await NewConversationAsync();
        var itemId = await ClaimedTicketAsync(conversationId);
        var (hub, notifier) = NewNotifier();

        // Tiket terminal tanpa balasan canonical (gagal tanpa permintaan maaf atau release host): worker mengabari
        // agar UI berhenti menunggu, bukan menggantung sampai reload.
        await using (var db = NewContext())
            await db.QueueItems.Where(q => q.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, Statuses.Failed));
        Assert.True(await notifier.PublishActivityAsync(conversationId, itemId, CancellationToken.None));
        Assert.Equal([NotifyTypes.ActivityChanged], hub.Sent.Select(s => s.Method));
        Assert.Equal(ChatNotifier.Group(conversationId), hub.Sent[0].Group);
        Assert.Empty(Assert.IsType<List<ActivityDto>>(hub.Sent[0].Payload));

        // Id tiket percakapan lain tidak pernah dipakai untuk menyalakan group ini.
        hub.Sent.Clear();
        Assert.False(await notifier.PublishActivityAsync(Guid.NewGuid(), itemId, CancellationToken.None));
        Assert.Empty(hub.Sent);

        // Dispatcher (recovery) memakai varian tanpa id tiket: snapshot apa adanya, tanpa validasi tiket.
        await notifier.PublishActivityAsync(conversationId, CancellationToken.None);
        Assert.Equal([NotifyTypes.ActivityChanged], hub.Sent.Select(s => s.Method));
        Assert.Empty(Assert.IsType<List<ActivityDto>>(hub.Sent[0].Payload));
    }

    [Fact]
    public async Task Activity_endpoint_needs_ownership_and_hides_terminal_tickets()
    {
        var conversationId = await NewConversationAsync();
        var pending = await EnqueueAsync(conversationId);
        var processing = await EnqueueAsync(conversationId);
        var done = await EnqueueAsync(conversationId);
        var failed = await EnqueueAsync(conversationId);
        var foreign = Guid.NewGuid();

        await using (var db = NewContext())
        {
            db.Conversations.Add(new Conversation { Id = foreign, UserId = "user-lain" });
            await db.SaveChangesAsync();
            await db.QueueItems.Where(q => q.Id == processing).ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, Statuses.Processing).SetProperty(q => q.LockedBy, RunId));
            await db.QueueItems.Where(q => q.Id == done).ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, Statuses.Done));
            await db.QueueItems.Where(q => q.Id == failed).ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, Statuses.Failed));
        }

        await using var check = NewContext();
        var rows = await ConversationEndpoints.ActiveTicketsForOwnerAsync(check, conversationId, "test", CancellationToken.None);
        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);
        Assert.Equal(await ActivityAsync(pending, Statuses.Pending), rows[0]);
        Assert.Equal(await ActivityAsync(processing, Statuses.Processing), rows[1]);

        // Percakapan user lain: null → handler menjawab 404, bukan daftar kosong atau daftar milik orang lain.
        Assert.Null(await ConversationEndpoints.ActiveTicketsForOwnerAsync(check, conversationId, "user-lain", CancellationToken.None));
        Assert.Null(await ConversationEndpoints.ActiveTicketsForOwnerAsync(check, foreign, "test", CancellationToken.None));
    }

    [Fact]
    public async Task Streaming_permission_is_captured_from_the_catalog_at_enqueue()
    {
        var conversationId = await NewConversationAsync();
        await using var db = NewContext();
        IReadOnlyList<ModelInfoDto> catalog = [new("model-stream", true, true, true, null, null), new("model-plain", true, false, true, null, null)];
        Task<IReadOnlyList<ModelInfoDto>> List(CancellationToken _) => Task.FromResult(catalog);

        Assert.Equal(202, (await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
            new("satu", null, "model-stream"), List, Policy(), CancellationToken.None)).Status);
        Assert.Equal(202, (await ConversationEndpoints.ValidateAndEnqueueAsync(db, "test", conversationId,
            new("dua", null, "model-plain"), List, Policy(), CancellationToken.None)).Status);

        var payloads = await db.QueueItems.AsNoTracking().OrderBy(q => q.Id).Select(q => q.PayloadJson).ToListAsync();
        Assert.Contains("\"supportsStreaming\":true", payloads[0]);
        Assert.Contains("\"supportsStreaming\":false", payloads[1]);
    }


    private static StreamEvent Batch(Guid conversationId, long itemId, int sequence, int offset, string? text,
        string kind = StreamEventKinds.Delta, string runId = RunId, Guid? streamId = null, int round = 0) =>
        new(conversationId, itemId, runId, streamId ?? Guid.NewGuid(), round, kind, sequence, offset, text);

    /// <summary>Tiket seperti diterima worker: status Processing dan <c>locked_by</c> = run pengirim.</summary>
    private async Task<long> ClaimedTicketAsync(Guid conversationId)
    {
        var (_, itemId) = await NewUserTicketAsync(conversationId, "halo");
        var claimed = await NewRepo().ClaimQueueItemAsync(RunId);
        Assert.NotNull(claimed);
        Assert.Equal(itemId, claimed!.Id);
        return itemId;
    }

    private async Task<long> AddMessageAsync(Guid conversationId, string content, bool visible, long itemId)
    {
        await using var db = NewContext();
        var message = new Message
        {
            ConversationId = conversationId, Role = MessageRoles.Assistant, Content = content,
            IsVisible = visible, QueueItemId = itemId,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    /// <summary>
    /// Snapshot activity yang diharapkan untuk tiket fixture: payload-nya selalu membawa alias model uji dan
    /// tiketnya bukan jawaban alternatif.
    /// </summary>
    private async Task<ActivityDto> ActivityAsync(long itemId, string status) =>
        new(itemId, status, (await GetItemAsync(itemId)).SourceMessageId, TestModel, QueueTypes.UserMessage, false);

    private (RecordingHub Hub, ChatNotifier Notifier) NewNotifier()
    {
        var hub = new RecordingHub();
        return (hub, new ChatNotifier(hub, new TestScopes(DbOptions), NullLogger<ChatNotifier>.Instance));
    }

    /// <summary>Hub tiruan: mencatat (method, group, payload) yang benar-benar dikirim ke client SignalR.</summary>
    private sealed class RecordingHub : IHubContext<ChatHub>
    {
        public List<SentEvent> Sent { get; } = [];
        public IHubClients Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException();

        public RecordingHub() => Clients = new RecordingClients(Sent);

        private sealed class RecordingClients(List<SentEvent> sent) : IHubClients
        {
            public IClientProxy All => throw new NotSupportedException();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Client(string connectionId) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
            public IClientProxy Group(string groupName) => new RecordingProxy(sent, groupName);
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
            public IClientProxy User(string userId) => throw new NotSupportedException();
            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();

            private sealed class RecordingProxy(List<SentEvent> sent, string group) : IClientProxy
            {
                public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken)
                {
                    sent.Add(new SentEvent(method, group, args.Length == 1 ? args[0] : args));
                    return Task.CompletedTask;
                }
            }
        }
    }

    private sealed record SentEvent(string Method, string Group, object? Payload);

    /// <summary>Scope tiruan berisi <see cref="AppDbContext"/> database test (dipakai ChatNotifier saat memuat snapshot).</summary>
    private sealed class TestScopes(DbContextOptions<AppDbContext> options) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(options);

        private sealed class Scope : IServiceScope
        {
            private readonly AppDbContext _db;

            public Scope(DbContextOptions<AppDbContext> options)
            {
                _db = new AppDbContext(options);
                ServiceProvider = new Provider(_db);
            }

            public IServiceProvider ServiceProvider { get; }
            public void Dispose() => _db.Dispose();

            private sealed class Provider(AppDbContext db) : IServiceProvider
            {
                public object? GetService(Type serviceType) => serviceType == typeof(AppDbContext) ? db : null;
            }
        }
    }
}
