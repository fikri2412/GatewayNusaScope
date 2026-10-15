using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Api.Dispatch;
using OperatorAi.Api.Hubs;
using OperatorAi.Api.Auth;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Api;

public static class ConversationEndpoints
{
    private const int MaxContentLength = 8000, MaxTitleLength = 200, AutoTitleLength = 60;

    public sealed record CreateConversationRequest(string? Title);
    public sealed record SendMessageRequest(string? Content, Guid[]? AttachmentIds, string? Model, string? ThinkMode = null);
    public sealed record RegenerateRequest(Guid? ClientRequestId);
    public sealed record CompareRequest(string[]? Models, Guid? ClientRequestId);

    /// <summary>Batas cabang compare (§4.3): satu panggilan AI penuh per cabang, jadi angkanya tetap kecil.</summary>
    private const int MaxCompareModels = 3;

    // Kode mesin galat baru; teks Indonesia-nya tetap sama dengan yang sudah dipakai jalur kirim.
    public const string NotRegenerableCode = "not_regenerable", ClientRequestIdConflictCode = "client_request_id_conflict",
        ClientRequestIdRequiredCode = "client_request_id_required", CompareInvalidCode = "compare_invalid",
        InvalidModelCode = "invalid_model", ModelUnavailableCode = "model_unavailable",
        VisionRequiredCode = "vision_required", CatalogUnavailableCode = "catalog_unavailable";

    private const string InvalidAliasMessage = "model wajib diisi dengan alias yang valid.";
    private const string VisionMessage =
        "Model ini belum dikonfirmasi mendukung gambar. Pilih model vision untuk lampiran gambar.";

    /// <summary>Hasil validasi + antre: <see cref="Error"/> tidak null berarti tidak ada baris yang ditulis.</summary>
    public sealed record SendOutcome(int Status, string? Error, Message? Message, long QueueItemId);

    /// <summary>Hasil regenerate: 202 tiket baru, 200 replay kunci yang sama, atau galat tanpa baris tertulis.</summary>
    public sealed record RegenerateOutcome(int Status, string? Error = null, string? Code = null, TurnEnqueued? Result = null);

    /// <summary>Hasil compare; <see cref="Branches"/> hanya diisi saat daftar model ditolak (400 compare_invalid).</summary>
    public sealed record CompareOutcome(int Status, string? Error = null, string? Code = null, CompareEnqueued? Result = null,
        IReadOnlyList<CompareBranchError>? Branches = null);

    /// <summary>Satu model yang tidak lolos validasi compare: kode mesin + pesan yang sama dengan jalur kirim.</summary>
    public sealed record CompareBranchError(string Model, string Code, string Error);

    /// <summary>Model yang sudah divalidasi: <see cref="Error"/> null berarti <see cref="Model"/> boleh dipakai.</summary>
    public sealed record ModelCheck(ModelInfoDto? Model = null, int Status = StatusCodes.Status200OK, string? Code = null,
        string? Error = null)
    {
        public bool Ok => Error is null;
    }

    public static void MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/conversations");
        g.MapPost("", CreateAsync);
        g.MapGet("", ListAsync);
        g.MapGet("/{id:guid}/messages", MessagesAsync);
        g.MapPost("/{id:guid}/messages", SendAsync);
        g.MapPost("/{id:guid}/messages/{messageId:long}/regenerate", RegenerateAsync);
        g.MapPost("/{id:guid}/messages/{messageId:long}/compare", CompareAsync);
        g.MapGet("/{id:guid}/jobs", JobsAsync);
        g.MapGet("/{id:guid}/activity", ActivityAsync);
        g.MapDelete("/{id:guid}", ConversationDeletion.HandleAsync);
        // Rute koleksi ikut dipetakan di sini supaya Program tidak perlu tahu dua grup endpoint.
        CollectionEndpoints.Map(app);
        ArtifactEndpoints.Map(app);
    }

    private static async Task<IResult> CreateAsync(CreateConversationRequest? req, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        var title = req?.Title?.Trim();
        if (title is { Length: > MaxTitleLength }) return Results.BadRequest(new { error = $"title maksimal {MaxTitleLength} karakter." });
        var c = new Conversation { UserId = http.User.UserId(), Title = string.IsNullOrEmpty(title) ? null : title };
        db.Conversations.Add(c);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/conversations/{c.Id}", c.ToDto());
    }

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var rows = await db.Conversations.AsNoTracking().Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToListAsync(ct);
        return Results.Ok(rows.Select(c => c.ToDto()));
    }

    /// <summary>Riwayat pesan yang terlihat. Publik (seperti helper lain di kelas ini) supaya isi DTO-nya diuji tanpa HTTP.</summary>
    public static async Task<IResult> MessagesAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!await Owns(db, id, http.User.UserId(), ct)) return Results.NotFound();
        var rows = await db.Messages.AsNoTracking().Where(m => m.ConversationId == id && m.IsVisible)
            .OrderBy(m => m.Id).ToListAsync(ct);
        return Results.Ok(await db.ToDtosAsync(rows, ct));
    }

    private static async Task<IResult> JobsAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!await Owns(db, id, http.User.UserId(), ct)) return Results.NotFound();
        var rows = await db.Jobs.AsNoTracking().Where(j => j.ConversationId == id).OrderBy(j => j.Id).ToListAsync(ct);
        return Results.Ok(rows.Select(j => j.ToDto()));
    }

    private static async Task<IResult> ActivityAsync(Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var rows = await ActiveTicketsForOwnerAsync(db, id, http.User.UserId(), ct);
        return rows is null ? Results.NotFound() : Results.Ok(rows);
    }

    /// <summary>
    /// Tiket Pending/Processing milik user untuk <c>GET /activity</c>; null berarti percakapan bukan miliknya (404).
    /// Dipisah dari handler supaya kepemilikan dan isinya bisa diuji tanpa HTTP.
    /// </summary>
    public static async Task<List<ActivityDto>?> ActiveTicketsForOwnerAsync(AppDbContext db, Guid conversationId,
        string userId, CancellationToken ct)
    {
        if (!await Owns(db, conversationId, userId, ct)) return null;
        return await db.ActiveTicketsAsync(conversationId, ct);
    }

    /// <summary>Pesan user + <c>queue_items</c> dalam satu transaksi; baru setelah commit dikirim SignalR dan dispatcher dibangunkan.</summary>
    private static async Task<IResult> SendAsync(Guid id, SendMessageRequest req, HttpContext http, AppDbContext db,
        ChatNotifier notifier, WorkerDispatcher dispatcher, GatewaySettingsProvider settings, GatewayModelsClient models,
        CancellationToken ct)
    {
        // Kebijakan dibaca sekali per permintaan: pesan dan tiketnya memakai snapshot yang sama walau settings berubah bersamaan.
        var outcome = await ValidateAndEnqueueAsync(db, http.User.UserId(), id, req, models.ListAsync, settings.CurrentPolicy, ct);
        if (outcome.Error is not null) return Results.Json(new { error = outcome.Error }, statusCode: outcome.Status);

        var dto = (await db.ToDtosAsync([outcome.Message!], ct))[0];
        await notifier.MessageCreatedAsync(id, dto);
        // Tiket sudah committed: snapshot dikirim sebelum dispatcher membangunkan worker supaya UI tidak menebak
        // status dari respons 202 saja (balapan dengan klaim worker).
        await notifier.PublishActivityAsync(id, ct);
        dispatcher.Signal();
        return Results.Accepted(value: new SendMessageResponse(dto, outcome.QueueItemId));
    }

    /// <summary>
    /// Kepemilikan → isi → lampiran → alias model (katalog diminta paling akhir), lalu pesan + tiket dalam satu transaksi.
    /// Katalog lewat delegasi supaya urutan validasi dan hasilnya bisa diuji tanpa HTTP; hasil error tidak menulis apa pun.
    /// </summary>
    public static async Task<SendOutcome> ValidateAndEnqueueAsync(AppDbContext db, string userId, Guid conversationId,
        SendMessageRequest req, Func<CancellationToken, Task<IReadOnlyList<ModelInfoDto>>> listModels, ExecutionPolicy policy,
        CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);
        if (conversation is null)
            return new(StatusCodes.Status404NotFound, "Percakapan tidak ditemukan.", null, 0);

        if (string.IsNullOrWhiteSpace(req.Content))
            return new(StatusCodes.Status400BadRequest, "content wajib diisi.", null, 0);
        if (req.Content.Length > MaxContentLength)
            return new(StatusCodes.Status400BadRequest, $"content maksimal {MaxContentLength} karakter.", null, 0);

        var upload = policy.Upload;
        // Batas jumlah dihitung dari panjang mentah sebelum Distinct: array berisi id yang sama tetap ditolak,
        // bukan menyusut jadi 1 lampiran lebih dulu.
        Guid[] requested = req.AttachmentIds ?? [];
        if (requested.Length > upload.MaxAttachmentsPerMessage)
            return new(StatusCodes.Status400BadRequest, $"Maksimal {upload.MaxAttachmentsPerMessage} lampiran per pesan.", null, 0);

        var attachmentIds = requested.Distinct().ToArray();
        List<Attachment> attachments = [];
        if (attachmentIds.Length > 0)
        {
            attachments = await db.Attachments.AsNoTracking()
                .Where(a => a.ConversationId == conversationId && attachmentIds.Contains(a.Id)).ToListAsync(ct);
            if (attachments.Count != attachmentIds.Length)
                return new(StatusCodes.Status400BadRequest, "attachmentIds harus milik percakapan ini.", null, 0);
            // Kebijakan saat ini, bukan saat lampiran diunggah: file lama yang tidak lagi memenuhi batas baru tidak boleh ikut terkirim.
            foreach (var attachment in attachments)
            {
                var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
                if (!upload.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    return new(StatusCodes.Status400BadRequest,
                        $"Lampiran \"{attachment.FileName}\" berformat {extension} yang tidak lagi diizinkan; yang diizinkan: {string.Join(", ", upload.AllowedExtensions)}.",
                        null, 0);
                if (attachment.SizeBytes > upload.MaxBytes)
                    return new(StatusCodes.Status400BadRequest,
                        $"Lampiran \"{attachment.FileName}\" melebihi ukuran maksimal {AttachmentEndpoints.FormatBytes(upload.MaxBytes)}.",
                        null, 0);
            }
        }

        if (TryNormalizeAlias(req.Model) is not { } alias)
            return new(StatusCodes.Status400BadRequest, InvalidAliasMessage, null, 0);

        var catalog = await TryCatalogAsync(listModels, ct);
        if (catalog is null)
            return new(StatusCodes.Status503ServiceUnavailable, ModelEndpoints.UnavailableMessage, null, 0);

        // Server tetap penentu walau UI basi: alias harus ada di daftar yang berlaku sekarang dan layak dipakai.
        var check = CheckModel(alias, catalog, attachments);
        if (!check.Ok) return new(check.Status, check.Error, null, 0);
        var model = check.Model!;

        var isAuto = alias.Equals(AutoAlias, StringComparison.OrdinalIgnoreCase);
        var reasoningEffort = req.ThinkMode?.ToLowerInvariant() switch { "deep" => "high", "fast" => "low", _ => null };
        var (message, queueItemId) = await EnqueueUserMessageAsync(db, conversation, req.Content, attachmentIds, model.Id, policy, ct,
            supportsStreaming: model.SupportsStreaming == true, supportsVision: model.SupportsVision == true,
            conversationModelAlias: isAuto ? AutoAlias : model.Id, reasoningEffort: reasoningEffort);
        return new(StatusCodes.Status202Accepted, null, message, queueItemId);
    }

    /// <summary>
    /// Pesan user + tiket <c>UserMessage</c> beserta snapshot kebijakan, model, dan id pesan sumbernya, dalam satu transaksi.
    /// Tiket baru selalu membawa kebijakan yang di-snapshot pemanggil, bukan setting terbaru saat worker berjalan;
    /// alias conversation ikut berubah hanya saat ada kiriman yang diterima.
    /// </summary>
    /// <param name="supportsStreaming">
    /// Izin streaming dari katalog saat enqueue (bukan setting terbaru). false berarti worker memakai jalur utuh.
    /// </param>
    public static async Task<(Message Message, long QueueItemId)> EnqueueUserMessageAsync(AppDbContext db, Conversation conversation,
        string content, Guid[] attachmentIds, string model, ExecutionPolicy policy, CancellationToken ct,
        bool supportsStreaming = false, bool supportsVision = false, string? conversationModelAlias = null,
        string? reasoningEffort = null)
    {
        var message = new Message
        {
            ConversationId = conversation.Id,
            Role = MessageRoles.User,
            Content = content,
            AttachmentIdsJson = attachmentIds.Length > 0 ? JsonSerializer.Serialize(attachmentIds) : null,
            ModelAlias = model,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (conversation.Title is null)
        {
            var title = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            conversation.Title = title.Length > AutoTitleLength ? title[..AutoTitleLength] : title;
        }
        conversation.ModelAlias = conversationModelAlias ?? model;
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct); // butuh message.Id untuk payload antrean dan jangkar pesan sumber

        var item = new QueueItem
        {
            ConversationId = conversation.Id,
            Type = QueueTypes.UserMessage,
            PayloadJson = JsonSerializer.Serialize(new { messageId = message.Id, model, supportsStreaming, supportsVision, reasoningEffort }),
            ExecutionPolicyJson = policy.ToJson(),
            SourceMessageId = message.Id,
        };
        db.QueueItems.Add(item);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (message, item.Id);
    }

    /// <summary>Jalur HTTP regenerate: validasi + antre dulu; snapshot aktivitas dan dispatcher baru setelah commit.</summary>
    private static async Task<IResult> RegenerateAsync(Guid id, long messageId, RegenerateRequest? req, HttpContext http,
        AppDbContext db, ChatNotifier notifier, WorkerDispatcher dispatcher, GatewaySettingsProvider settings,
        GatewayModelsClient models, CancellationToken ct)
    {
        var outcome = await RegenerateTurnAsync(db, http.User.UserId(), id, messageId, req?.ClientRequestId,
            models.ListAsync, settings.CurrentPolicy, ct);
        if (outcome.Error is not null) return Fail(outcome.Status, outcome.Code, outcome.Error);

        // Baris sudah committed: seperti kirim pesan, aktivitas dikabarkan sebelum dispatcher membangunkan worker.
        await notifier.PublishActivityAsync(id, ct);
        dispatcher.Signal();
        return Results.Json(outcome.Result, statusCode: outcome.Status);
    }

    /// <summary>
    /// Antre satu tiket <c>UserMessage</c> alternatif untuk menjawab ulang pesan sumber (§4.3): tanpa pesan user
    /// baru, <c>is_primary = 0</c>, dan <c>clientRequestId</c> membuat permintaan ulang tidak menulis baris kedua.
    /// </summary>
    public static async Task<RegenerateOutcome> RegenerateTurnAsync(AppDbContext db, string userId, Guid conversationId,
        long messageId, Guid? clientRequestId, Func<CancellationToken, Task<IReadOnlyList<ModelInfoDto>>> listModels,
        ExecutionPolicy policy, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);
        if (conversation is null) return new(StatusCodes.Status404NotFound, "Percakapan tidak ditemukan.");

        var anchor = await AnchorAsync(db, conversationId, messageId, ct);
        if (anchor is null) return new(StatusCodes.Status404NotFound, "Pesan tidak ditemukan.");
        if (anchor.Role != MessageRoles.Assistant)
            return new(StatusCodes.Status400BadRequest, "Hanya jawaban asisten yang bisa dijawab ulang.", NotRegenerableCode);
        var source = await SourceMessageIdAsync(db, anchor, ct);
        if (source is not { } sourceId)
            return new(StatusCodes.Status400BadRequest, "Jawaban ini tidak punya pesan sumber, jadi tidak bisa dijawab ulang.",
                NotRegenerableCode);
        if (clientRequestId is not { } key)
            return new(StatusCodes.Status400BadRequest, "clientRequestId wajib diisi.", ClientRequestIdRequiredCode);

        var keyText = key.ToString();
        var replay = await db.QueueItems.AsNoTracking()
            .FirstOrDefaultAsync(q => q.ConversationId == conversationId && q.ClientRequestId == keyText, ct);
        if (replay is not null) return ReplayTurn(replay, sourceId);

        // Model jawaban yang lama; percakapan hanya jadi cadangan untuk baris yang belum punya alias.
        var check = await CheckModelAsync(db, anchor.ModelAlias ?? conversation.ModelAlias, sourceId, listModels, ct);
        if (!check.Ok) return new(check.Status, check.Error, check.Code);

        var item = NewTurnItem(conversationId, sourceId, check.Model!, policy, isPrimary: false, keyText);
        db.QueueItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Balapan dua permintaan berkunci sama: yang kalah membaca baris pemenang lalu menerapkan aturan replay.
            db.ChangeTracker.Clear();
            var winner = await db.QueueItems.AsNoTracking()
                .FirstOrDefaultAsync(q => q.ConversationId == conversationId && q.ClientRequestId == keyText, ct);
            if (winner is null) throw;
            return ReplayTurn(winner, sourceId);
        }
        return new(StatusCodes.Status202Accepted, Result: new TurnEnqueued(item.Id, sourceId, check.Model!.Id));
    }

    /// <summary>Replay regenerate: kunci dan sumber sama → 200 dengan tiket yang sudah ada; sumber beda → 409.</summary>
    private static RegenerateOutcome ReplayTurn(QueueItem existing, long sourceMessageId) =>
        existing.SourceMessageId == sourceMessageId
            ? new(StatusCodes.Status200OK,
                Result: new TurnEnqueued(existing.Id, sourceMessageId, Mapping.PayloadModel(existing.PayloadJson) ?? ""))
            : new(StatusCodes.Status409Conflict, "clientRequestId sudah dipakai untuk pesan sumber lain.",
                ClientRequestIdConflictCode);

    /// <summary>Jalur HTTP compare: satu tiket per model; snapshot aktivitas dan dispatcher baru setelah commit.</summary>
    private static async Task<IResult> CompareAsync(Guid id, long messageId, CompareRequest? req, HttpContext http,
        AppDbContext db, ChatNotifier notifier, WorkerDispatcher dispatcher, GatewaySettingsProvider settings,
        GatewayModelsClient models, CancellationToken ct)
    {
        var outcome = await CompareTurnAsync(db, http.User.UserId(), id, messageId, req?.Models ?? [], req?.ClientRequestId,
            models.ListAsync, settings.CurrentPolicy, ct);
        if (outcome.Error is not null)
            return outcome.Branches is { } branches
                ? Results.Json(new { error = outcome.Error, code = outcome.Code, branches }, statusCode: outcome.Status)
                : Fail(outcome.Status, outcome.Code, outcome.Error);

        await notifier.PublishActivityAsync(id, ct);
        dispatcher.Signal();
        return Results.Json(outcome.Result, statusCode: outcome.Status);
    }

    /// <summary>
    /// Antre satu tiket <c>UserMessage</c> alternatif per model dalam satu transaksi (§4.3). Semua cabang divalidasi
    /// lebih dulu: satu model ditolak berarti tidak ada baris yang ditulis, bukan jawaban setengah jadi.
    /// </summary>
    public static async Task<CompareOutcome> CompareTurnAsync(AppDbContext db, string userId, Guid conversationId,
        long messageId, string[] models, Guid? clientRequestId,
        Func<CancellationToken, Task<IReadOnlyList<ModelInfoDto>>> listModels, ExecutionPolicy policy, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);
        if (conversation is null) return new(StatusCodes.Status404NotFound, "Percakapan tidak ditemukan.");

        var anchor = await AnchorAsync(db, conversationId, messageId, ct);
        if (anchor is null) return new(StatusCodes.Status404NotFound, "Pesan tidak ditemukan.");
        var source = await SourceMessageIdAsync(db, anchor, ct);
        if (source is not { } sourceId)
            return new(StatusCodes.Status400BadRequest, "Jawaban ini tidak punya pesan sumber, jadi tidak bisa dibandingkan.",
                NotRegenerableCode);
        if (clientRequestId is not { } key)
            return new(StatusCodes.Status400BadRequest, "clientRequestId wajib diisi.", ClientRequestIdRequiredCode);
        if (models.Length is < 1 or > MaxCompareModels)
            return new(StatusCodes.Status400BadRequest, $"models harus berisi 1 sampai {MaxCompareModels} model.",
                CompareInvalidCode, Branches: []);
        if (models.Distinct(StringComparer.Ordinal).Count() != models.Length)
            return new(StatusCodes.Status400BadRequest, "Model dalam models tidak boleh duplikat.", CompareInvalidCode,
                Branches: []);

        var keyText = key.ToString();
        var keys = CompareKeys(keyText);
        var first = await db.QueueItems.AsNoTracking()
            .FirstOrDefaultAsync(q => q.ConversationId == conversationId && q.ClientRequestId == keys[0], ct);
        if (first is not null) return await ReplayCompareAsync(db, conversationId, sourceId, keyText, first, ct);

        var catalog = await TryCatalogAsync(listModels, ct);
        if (catalog is null)
            return new(StatusCodes.Status503ServiceUnavailable, ModelEndpoints.UnavailableMessage, CatalogUnavailableCode);

        var attachments = await SourceAttachmentsAsync(db, sourceId, ct);
        var checks = models.Select(m => CheckModel(m, catalog, attachments)).ToList();
        var rejected = new List<CompareBranchError>();
        for (var i = 0; i < checks.Count; i++)
            if (!checks[i].Ok) rejected.Add(new(models[i], checks[i].Code!, checks[i].Error!));
        if (rejected.Count > 0)
            return new(StatusCodes.Status400BadRequest, "Ada model yang tidak bisa dipakai.", CompareInvalidCode,
                Branches: rejected);

        var items = checks.Select((check, index) => NewTurnItem(conversationId, sourceId, check.Model!, policy,
            isPrimary: false, keys[index])).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.QueueItems.AddRange(items);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            first = await db.QueueItems.AsNoTracking()
                .FirstOrDefaultAsync(q => q.ConversationId == conversationId && q.ClientRequestId == keys[0], ct);
            if (first is null) throw;
            return await ReplayCompareAsync(db, conversationId, sourceId, keyText, first, ct);
        }
        return new(StatusCodes.Status202Accepted, Result: new CompareEnqueued(sourceId,
            items.Select((item, index) => new CompareBranch(item.Id, checks[index].Model!.Id)).ToList()));
    }

    /// <summary>Kunci cabang yang mungkin dipakai satu permintaan compare: <c>{key}:0</c> sampai <c>{key}:2</c>.</summary>
    private static List<string> CompareKeys(string keyText) =>
        Enumerable.Range(0, MaxCompareModels).Select(index => $"{keyText}:{index}").ToList();

    /// <summary>
    /// Replay compare: kunci cabang pertama sudah ada. Sumber sama → 200 dengan cabang yang tersimpan; sumber beda → 409.
    /// </summary>
    private static async Task<CompareOutcome> ReplayCompareAsync(AppDbContext db, Guid conversationId, long sourceMessageId,
        string keyText, QueueItem first, CancellationToken ct)
    {
        if (first.SourceMessageId != sourceMessageId)
            return new(StatusCodes.Status409Conflict, "clientRequestId sudah dipakai untuk pesan sumber lain.",
                ClientRequestIdConflictCode);

        var keys = CompareKeys(keyText);
        var rows = await db.QueueItems.AsNoTracking()
            .Where(q => q.ConversationId == conversationId && keys.Contains(q.ClientRequestId!)).ToListAsync(ct);
        var branches = keys.Select(k => rows.FirstOrDefault(r => r.ClientRequestId == k)).OfType<QueueItem>()
            .Select(r => new CompareBranch(r.Id, Mapping.PayloadModel(r.PayloadJson) ?? "")).ToList();
        return new(StatusCodes.Status200OK, Result: new CompareEnqueued(sourceMessageId, branches));
    }

    /// <summary>
    /// Urutan validasi model <c>POST /messages</c> untuk satu pesan sumber: bentuk alias → katalog (503 saat gagal)
    /// → kelayakan → syarat vision dari lampiran sumber. Jalur regenerate memakai ini, compare memanggil per cabang.
    /// </summary>
    public static async Task<ModelCheck> CheckModelAsync(AppDbContext db, string? alias, long sourceMessageId,
        Func<CancellationToken, Task<IReadOnlyList<ModelInfoDto>>> listModels, CancellationToken ct)
    {
        if (TryNormalizeAlias(alias) is not { } normalized)
            return new(null, StatusCodes.Status400BadRequest, InvalidModelCode, InvalidAliasMessage);
        var catalog = await TryCatalogAsync(listModels, ct);
        if (catalog is null)
            return new(null, StatusCodes.Status503ServiceUnavailable, CatalogUnavailableCode, ModelEndpoints.UnavailableMessage);
        return CheckModel(normalized, catalog, await SourceAttachmentsAsync(db, sourceMessageId, ct));
    }

    public const string AutoAlias = "auto";

    /// <summary>Validasi satu alias terhadap katalog yang sudah dibaca: bentuk, kelayakan, lalu syarat vision.</summary>
    public static ModelCheck CheckModel(string? alias, IReadOnlyList<ModelInfoDto> catalog,
        IReadOnlyList<Attachment> sourceAttachments)
    {
        if (TryNormalizeAlias(alias) is not { } normalized)
            return new(null, StatusCodes.Status400BadRequest, InvalidModelCode, InvalidAliasMessage);
        if (normalized.Equals(AutoAlias, StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveAutoModel(catalog, sourceAttachments);
            if (resolved is null)
            {
                var needsVision = sourceAttachments.Any(a => a.ContentType.StartsWith("image/", StringComparison.Ordinal));
                return needsVision
                    ? new(null, StatusCodes.Status400BadRequest, VisionRequiredCode, "Tidak ada model aktif yang mendukung vision untuk menganalisis gambar ini.")
                    : new(null, StatusCodes.Status400BadRequest, ModelUnavailableCode, "Tidak ada model aktif yang memenuhi syarat untuk pemilihan otomatis saat ini.");
            }
            return new(resolved);
        }
        var model = FindEligible(catalog, normalized);
        if (model is null)
            return new(null, StatusCodes.Status400BadRequest, ModelUnavailableCode, UnavailableModelMessage(normalized));
        if (model.SupportsVision != true && sourceAttachments.Any(a => a.ContentType.StartsWith("image/", StringComparison.Ordinal)))
            return new(null, StatusCodes.Status400BadRequest, VisionRequiredCode, VisionMessage);
        return new(model);
    }

    /// <summary>Pilih model otomatis berdasarkan kapabilitas yang dibutuhkan (vision untuk gambar, alat, kesiapan).</summary>
    public static ModelInfoDto? ResolveAutoModel(IReadOnlyList<ModelInfoDto> catalog, IReadOnlyList<Attachment> sourceAttachments)
    {
        var eligible = catalog.Where(m => m.Ready && m.SupportsTools == true).ToList();
        if (eligible.Count == 0) return null;
        var needsVision = sourceAttachments.Any(a => a.ContentType.StartsWith("image/", StringComparison.Ordinal));
        if (needsVision)
        {
            return eligible.FirstOrDefault(m => m.SupportsVision == true && m.Id.Contains("glm", StringComparison.OrdinalIgnoreCase))
                ?? eligible.FirstOrDefault(m => m.SupportsVision == true);
        }
        return eligible.FirstOrDefault(m => m.Id.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            ?? eligible.FirstOrDefault(m => m.Id.Contains("glm", StringComparison.OrdinalIgnoreCase))
            ?? eligible.FirstOrDefault();
    }

    /// <summary>Alias ternormalisasi; null berarti bentuknya tidak sah. Katalog tidak pernah diminta untuk alias tidak sah.</summary>
    public static string? TryNormalizeAlias(string? alias)
    {
        try
        {
            return ModelSnapshot.Validate(alias);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Katalog dari delegasi; null berarti tidak bisa dibaca (503), bukan katalog kosong.</summary>
    public static async Task<IReadOnlyList<ModelInfoDto>?> TryCatalogAsync(
        Func<CancellationToken, Task<IReadOnlyList<ModelInfoDto>>> listModels, CancellationToken ct)
    {
        try
        {
            return await listModels(ct);
        }
        catch (GatewayModelsException)
        {
            return null;
        }
    }

    /// <summary>Model dari katalog yang layak dikirim (route siap dan tools sudah dikonfirmasi); null berarti tidak.</summary>
    public static ModelInfoDto? FindEligible(IReadOnlyList<ModelInfoDto> catalog, string alias)
    {
        var model = catalog.FirstOrDefault(m => m.Id == alias);
        return model is not null && ModelEndpoints.IsEligible(model) ? model : null;
    }

    private static string UnavailableModelMessage(string alias) =>
        $"Model \"{alias}\" tidak tersedia atau tidak diizinkan.";

    /// <summary>Badan galat: teks Indonesia + kode mesin (mis. <c>conversation_busy</c>) yang dipakai UI.</summary>
    private static IResult Fail(int status, string? code, string error) =>
        Results.Json(new { error, code }, statusCode: status);

    /// <summary>Pesan jangkar yang terlihat milik percakapan ini; null berarti 404 (tidak ada atau bukan miliknya).</summary>
    private static Task<Message?> AnchorAsync(AppDbContext db, Guid conversationId, long messageId, CancellationToken ct) =>
        db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == conversationId && m.IsVisible, ct);

    /// <summary>
    /// Pesan sumber: pesan user adalah sumber dirinya sendiri, baris jawaban memakai <c>source_message_id</c> tiket
    /// yang menulisnya (null bila tiketnya tidak ada atau tidak punya sumber).
    /// </summary>
    private static async Task<long?> SourceMessageIdAsync(AppDbContext db, Message anchor, CancellationToken ct)
    {
        if (anchor.Role == MessageRoles.User) return anchor.Id;
        if (anchor.QueueItemId is not { } itemId) return null;
        return await db.QueueItems.AsNoTracking().Where(q => q.Id == itemId).Select(q => q.SourceMessageId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Lampiran yang dirujuk pesan sumber (snapshot id di barisnya); dipakai hanya untuk syarat vision.</summary>
    public static async Task<List<Attachment>> SourceAttachmentsAsync(AppDbContext db, long messageId, CancellationToken ct)
    {
        var json = await db.Messages.AsNoTracking().Where(m => m.Id == messageId).Select(m => m.AttachmentIdsJson)
            .FirstOrDefaultAsync(ct);
        var ids = Mapping.ParseIds(json);
        return ids.Length == 0
            ? []
            : await db.Attachments.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
    }

    /// <summary>
    /// Baris antrean jawaban alternatif: payload seperti jalur kirim tapi menunjuk pesan sumbernya, <c>is_primary = 0</c>,
    /// dan kunci idempotensi klien. Tidak ada pesan user baru dan alias percakapan tidak ikut berubah.
    /// </summary>
    private static QueueItem NewTurnItem(Guid conversationId, long sourceMessageId, ModelInfoDto model,
        ExecutionPolicy policy, bool isPrimary, string clientRequestId) => new()
    {
        ConversationId = conversationId,
        Type = QueueTypes.UserMessage,
        PayloadJson = JsonSerializer.Serialize(new
        {
            messageId = sourceMessageId,
            model = model.Id,
            supportsStreaming = model.SupportsStreaming == true,
            supportsVision = model.SupportsVision == true,
        }),
        ExecutionPolicyJson = policy.ToJson(),
        SourceMessageId = sourceMessageId,
        ClientRequestId = clientRequestId,
        IsPrimary = isPrimary,
    };

    private static Task<bool> Owns(AppDbContext db, Guid conversationId, string userId, CancellationToken ct) =>
        db.Conversations.AnyAsync(c => c.Id == conversationId && c.UserId == userId, ct);
}
