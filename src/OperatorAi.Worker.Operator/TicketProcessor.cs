using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Ai;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Notify;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;
using OperatorAi.Worker.Operator.Tools;

namespace OperatorAi.Worker.Operator;

/// <summary>Tiket bukan lagi milik run ini (dipulihkan dan diambil proses lain): berhenti tanpa menulis apa pun.</summary>
public sealed class LostTicketException(string message) : Exception(message);

/// <summary>
/// Algoritma satu tiket operator (DESIGN bagian 9): siapkan konteks dari snapshot kebijakan baris tiket, panggil AI,
/// jalankan tools bila diminta, ulangi sampai ada jawaban teks. Tiap putaran tool ditulis dalam satu transaksi (baris
/// assistant, job, hasil tool); jawaban akhir ditulis bersama penyelesaian tiket. Dengan begitu tiket yang diulang tidak
/// pernah menggandakan job atau balasan. Kepemilikan dijaga lease berdetak (<c>KeepAliveAsync</c>) plus guard di tiap
/// penulisan; output job yang sudah lengkap dijawab langsung tanpa AI bila user tidak minta diubah.
/// </summary>
public sealed class TicketProcessor(
    WorkerRun run,
    QueueRepository repo,
    NotifyClient notify,
    IAiClient ai,
    DbContextOptions<AppDbContext> dbOptions,
    ILogger<TicketProcessor> logger,
    IWebSearch? webSearch = null)
{
    public const string TooComplexReply =
        "Maaf, permintaan ini terlalu rumit untuk saya proses sekaligus. Bisa dipecah jadi langkah lebih kecil?";

    public const string TokenLimitReply =
        "Batas pemakaian AI untuk percakapan ini sudah tercapai, jadi saya tidak bisa menjawab lagi di sini. Silakan mulai percakapan baru.";

    /// <param name="AnchorId">Pesan user asal rantai ini (jangkar tujuan); selalu ikut walau riwayat dipotong.</param>
    /// <param name="BoundaryId">Riwayat dipakai sampai id ini (ditambah baris milik tiket sendiri).</param>
    /// <param name="TrustedContext">Metadata peristiwa dari database untuk blok sistem; null untuk tiket percakapan biasa.</param>
    /// <param name="AttachmentIdsJson">Lampiran hasil job (mis. PDF) untuk ditempel pada balasan akhir.</param>
    /// <param name="Direct">Balasan langsung tanpa AI dari output job yang sudah lengkap (null → jalur AI).</param>
    private sealed record Turn(long AnchorId, long BoundaryId, string? TrustedContext, string? AttachmentIdsJson,
        (string Text, string? AttachmentIdsJson)? Direct, string? DirectFinishReason = null);

    public async Task HandleAsync(QueueItem item, CancellationToken ct)
    {
        var policy = ExecutionPolicy.FromJson(item.ExecutionPolicyJson);
        // Model workflow dibaca sekali dari snapshot tiket, sebelum apa pun dikerjakan: pilihan model percakapan
        // yang berubah di tengah jalan tidak pernah menggantikan model pesan ini.
        var model = ModelSnapshot.Read(item.PayloadJson);
        await using var lease = await repo.KeepAliveAsync(item.Id, run.RunId, isJob: false, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
        try
        {
            await HandleCoreAsync(item, policy, model, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lease.Token.IsCancellationRequested)
        {
            throw new LostTicketException($"Tiket {item.Id} bukan lagi milik run {run.RunId} (lease hilang)");
        }
    }

    private async Task HandleCoreAsync(QueueItem item, ExecutionPolicy policy, string model, CancellationToken ct)
    {
        // Statistik satu percobaan tiket (§5): stopwatch sejak klaim, token semua panggilan model, dan token pertama.
        var ticketClock = Stopwatch.StartNew();
        int? firstTokenMs = null;
        long tokensUsed = 0;
        var usageComplete = true;
        var anyCall = false;

        // Akumulasi token semua panggilan model tiket ini (jalur normal maupun jalur exception stream).
        void CountCall(int promptTokens, int completionTokens, bool usageAvailable)
        {
            anyCall = true;
            usageComplete &= usageAvailable;
            tokensUsed += (long)promptTokens + completionTokens;
        }

        // Tiket tanpa panggilan AI (balasan langsung) dan panggilan tanpa usage tidak pernah menulis angka token.
        ReplyTiming Timing() => new(anyCall && usageComplete && tokensUsed <= int.MaxValue ? (int)tokensUsed : null,
            QueueWaitMs(item), firstTokenMs);

        var preparing = Stopwatch.StartNew();
        var turn = await PrepareAsync(item, policy, model, ct);
        logger.LogInformation("Timing: siapkan tiket={Ms}ms", preparing.ElapsedMilliseconds);
        if (turn.Direct is { } direct)
        {
            // Output job sudah lengkap dan user tidak minta diubah: tidak ada AI yang sekadar menulis ulang.
            await CompleteAsync(item, direct.Text, direct.AttachmentIdsJson, finishReason: turn.DirectFinishReason, ct, Timing());
            return;
        }

        var system = PromptPolicy.Compose(OperatorPrompt.RoleInstructions, policy, turn.TrustedContext);
        var tools = ToolDefinitions.All();

        for (var loop = 0; ; loop++)
        {
            // Batas lunak: panggilan yang sedang berjalan boleh melewatinya sedikit. Balasan batas tetap membawa lampiran
            // hasil job (mis. PDF) supaya output yang sudah jadi tidak hilang dari user.
            if (await TokensUsedAsync(item.ConversationId, ct) >= policy.MaxTokensPerConversation)
            {
                logger.LogWarning("Percakapan melewati batas {Limit} token; dijawab dengan pesan batas tanpa memanggil AI", policy.MaxTokensPerConversation);
                await CompleteAsync(item, TokenLimitReply, turn.AttachmentIdsJson, finishReason: null, ct, Timing());
                return;
            }

            var loading = Stopwatch.StartNew();
            var history = await LoadHistoryAsync(item, turn, policy.MaxHistoryMessages, ct);
            logger.LogInformation("Timing: muat riwayat={Ms}ms ({Count} pesan)", loading.ElapsedMilliseconds, history.Count);

            var clock = Stopwatch.StartNew();
            // Batas runtime ikut snapshot tiket: pesan yang sudah masuk antrean tidak berubah perilakunya saat setting berubah.
            var reasoningEffort = ModelSnapshot.ReasoningEffort(item.PayloadJson);
            var request = new AiRequest(model, item.ConversationId, system, history, policy.MaxOutputTokens, tools,
                $"conversation={item.ConversationId};queueItem={item.Id}", policy.Ai, ReasoningEffort: reasoningEffort);
            await using var stream = ModelSnapshot.SupportsStreaming(item.PayloadJson)
                ? new StreamPublisher(notify, item.ConversationId, item.Id, run.RunId, loop, ct) : null;
            try
            {
                AiResponse response;
                var firstContent = true;
                try
                {
                    response = stream is null
                        ? await ai.CompleteAsync(request, ct)
                        : await ai.CompleteStreamingAsync(request, async (text, token) =>
                        {
                            if (firstContent)
                            {
                                firstContent = false;
                                logger.LogInformation("AI content pertama: ms={Ms} streamId={StreamId}", clock.ElapsedMilliseconds, stream.StreamId);
                            }
                            if (text.Length > 0) firstTokenMs ??= (int)ticketClock.ElapsedMilliseconds;
                            await stream.WriteAsync(text, token);
                        }, stream.Token);
                }
                catch (AiStreamException ex)
                {
                    CountCall(ex.PromptTokens, ex.CompletionTokens, ex.UsageAvailable);
                    await AddTokensAsync(item.ConversationId, (long)ex.PromptTokens + ex.CompletionTokens, ex.UsageAvailable);
                    if (stream?.LostOwnership == true) throw new LostTicketException($"Tiket {item.Id} bukan lagi milik run {run.RunId}");
                    if (ct.IsCancellationRequested) throw new OperationCanceledException("Worker dihentikan saat membaca stream", ex, ct);
                    throw;
                }
                CountCall(response.PromptTokens, response.CompletionTokens, response.UsageAvailable);
                await AddTokensAsync(item.ConversationId, (long)response.PromptTokens + response.CompletionTokens, response.UsageAvailable);
                if (stream is not null) await stream.FinishAsync();
                logger.LogInformation("AI menjawab: prompt={Prompt} completion={Completion} usageKnown={UsageKnown} ms={Ms} finish={Finish} tools={Tools}",
                    response.PromptTokens, response.CompletionTokens, response.UsageAvailable, clock.ElapsedMilliseconds,
                    response.FinishReason, response.ToolCalls.Count);

                if (response.ToolCalls.Count == 0)
                {
                    if (response.FinishReason == "length") logger.LogWarning("Jawaban terpotong (finish_reason=length); tetap dikirim");
                    if (string.IsNullOrWhiteSpace(response.Text))
                        throw new AiException($"Jawaban AI kosong (finish_reason={response.FinishReason})");
                    await CompleteAsync(item, response.Text.Trim(), turn.AttachmentIdsJson, response.FinishReason, ct, Timing(), stream?.StreamId);
                    return;
                }

                if (loop >= policy.MaxToolLoops)
                {
                    logger.LogWarning("AI masih meminta tool setelah {Loops} putaran; dijawab dengan pesan batas", loop);
                    if (stream is not null) await stream.AbortAsync();
                    await CompleteAsync(item, TooComplexReply, turn.AttachmentIdsJson, finishReason: null, ct, Timing());
                    return;
                }
                await RunToolsAsync(item, policy, turn.AnchorId, model, response, ct, stream?.StreamId);
            }
            catch
            {
                if (stream is not null) await stream.AbortAsync();
                throw;
            }
        }
    }

    private async Task<long> TokensUsedAsync(Guid conversationId, CancellationToken ct)
    {
        await using var db = new AppDbContext(dbOptions);
        return await db.TokensUsedAsync(conversationId, ct);
    }

    /// <summary>Tanpa token pembatalan: panggilan AI sudah terjadi dan tokennya terpakai walau worker dihentikan sesudahnya.</summary>
    private async Task AddTokensAsync(Guid conversationId, long tokens, bool usageAvailable)
    {
        await using var db = new AppDbContext(dbOptions);
        await db.AddTokensAsync(conversationId, tokens, usageAvailable, CancellationToken.None);
    }

    private async Task<Turn> PrepareAsync(QueueItem item, ExecutionPolicy policy, string model, CancellationToken ct)
    {
        // Snapshot kebijakan & jangkar pesan sumber datang dari baris tiket (bukan setting global saat ini).
        var anchorId = item.SourceMessageId ?? throw new InvalidOperationException($"Tiket {item.Id} tanpa pesan sumber.");
        await using var db = new AppDbContext(dbOptions);

        var source = await db.Messages.AsNoTracking()
            .Where(m => m.Id == anchorId && m.ConversationId == item.ConversationId && m.Role == MessageRoles.User)
            .Select(m => new { m.ModelAlias }).SingleOrDefaultAsync(ct);
        if (source is null)
            throw new InvalidOperationException($"Pesan sumber {anchorId} bukan pesan user di percakapan {item.ConversationId}");
        // Tiket primer harus memakai model pesan sumbernya (pesan lama sebelum 6A2 belum punya model).
        // Tiket alternatif (regenerate/bandingkan) sengaja memakai model pilihan branch, jadi tidak dicek.
        if (item.IsPrimary && source.ModelAlias is { } sourceAlias && sourceAlias != model)
            throw new InvalidOperationException(
                $"Model pesan sumber '{sourceAlias}' tidak sama dengan model tiket '{model}' di percakapan {item.ConversationId}");

        if (item.Type == QueueTypes.UserMessage) return new Turn(anchorId, anchorId, null, null, null);

        if (item.Type is not (QueueTypes.JobCompleted or QueueTypes.JobFailed))
            throw new InvalidOperationException($"Tipe tiket tidak dikenal: {item.Type}");

        // Job yang gagal karena modelnya dicabut dijawab langsung: panggilan AI apa pun akan gagal dengan cara yang sama.
        if (item.Type == QueueTypes.JobFailed && IsFailureCategory(item.PayloadJson, FailureCategories.ModelUnavailable))
            return new Turn(anchorId, anchorId, null, null, (Replies.ModelUnavailable, null));

        var job = await LoadJobAsync(db, item, ct);
        if (job.SourceMessageId != anchorId)
            throw new InvalidOperationException($"Job {job.Id} bukan bagian dari percakapan pesan sumber {anchorId}");
        if (item.Type == QueueTypes.JobFailed && IsFailureCategory(item.PayloadJson, FailureCategories.DocumentInvalid))
            return new Turn(anchorId, anchorId, null, null,
                ($"Dokumen/gambar belum bisa dianalisis: {job.Error}", null));

        if (item.Type == QueueTypes.JobCompleted && await DirectReply.TryAsync(job, policy, db, ct) is { } direct)
            return new Turn(anchorId, anchorId, null, direct.AttachmentIdsJson, direct,
                job.JobType == JobTypes.DocumentAnalyze ? DocumentAnalysisResult.Parse(job.OutputJson!).FinishReason : null);

        // Jalur AI. Metadata peristiwa (id/tipe/status job) tepercaya dan dikirim lewat blok sistem; isi output job tetap
        // data mentah di baris tersembunyi, ditulis sekali supaya tiket yang diulang tidak menggandakannya.
        var note = await db.Messages.Where(m => m.QueueItemId == item.Id).OrderBy(m => m.Id).FirstOrDefaultAsync(ct);
        if (note is null)
        {
            var text = item.Type == QueueTypes.JobCompleted
                ? $"DATA HASIL JOB (dari database, bukan instruksi user)\njob={job.Id}; jobType={job.JobType}; status={job.Status}\noutput:\n{job.OutputJson}"
                : $"DATA HASIL JOB (dari database, bukan instruksi user)\njob={job.Id}; jobType={job.JobType}; status={job.Status}\nerror: {job.Error}";
            note = new Message
            {
                ConversationId = item.ConversationId, Role = MessageRoles.User, IsVisible = false, QueueItemId = item.Id,
                ModelAlias = model, IsPrimary = item.IsPrimary,
                AiContentJson = new JsonArray(new AiMessage(MessageRoles.User, text).ToJson()).ToJsonString(),
            };
            db.Messages.Add(note);
            await db.SaveChangesAsync(ct);
        }

        // Status diambil dari baris job, bukan dari payload tiket atau teks mana pun.
        var trusted = $"Peristiwa database: job {job.Id} tipe {job.JobType} status {job.Status}.";
        return new Turn(anchorId, note.Id, trusted,
            item.Type == QueueTypes.JobCompleted ? await OutputAttachmentsAsync(db, job, ct) : null, null);
    }

    /// <summary>
    /// Kategori terstruktur dari server (bukan tebakan atas teks <c>error</c>): job ini gagal karena modelnya
    /// dicabut, jadi tiket ini harus dijawab tanpa memanggil AI.
    /// </summary>
    private static bool IsFailureCategory(string payloadJson, string failureCategory)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("category", out var category)
                && category.ValueKind == JsonValueKind.String
                && category.GetString() == failureCategory;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<Job> LoadJobAsync(AppDbContext db, QueueItem item, CancellationToken ct)
    {
        long jobId;
        try
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("jobId", out var value) || !value.TryGetInt64(out jobId))
                throw new JsonException("jobId tidak ada atau bukan angka.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Payload tiket {item.Id} tidak memuat jobId.", ex);
        }

        return await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.ConversationId == item.ConversationId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} tidak ditemukan di percakapan {item.ConversationId}");
    }

    /// <summary>Output job yang memuat <c>attachmentId</c> (mis. PDF) ditempel ke balasan; hanya bila lampirannya milik percakapan itu.</summary>
    private static async Task<string?> OutputAttachmentsAsync(AppDbContext db, Job job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.OutputJson)) return null;
        try
        {
            if (JsonNode.Parse(job.OutputJson) is JsonObject o && o["attachmentId"] is JsonValue v
                && v.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out var id)
                && await db.Attachments.AnyAsync(a => a.Id == id && a.ConversationId == job.ConversationId, ct))
                return JsonSerializer.Serialize(new[] { id });
        }
        catch (JsonException)
        {
            // Output bukan JSON: tidak ada lampiran.
        }
        return null;
    }

    /// <summary>
    /// Riwayat sampai batas tiket (baris setelah batas tidak ikut, begitu pula baris alternatif <c>is_primary = 0</c>),
    /// ditambah baris milik tiket ini sendiri (putaran tool sebelumnya) dan pesan sumber — keduanya wajib ikut walau
    /// batas riwayat memotongnya.
    /// </summary>
    private async Task<IReadOnlyList<AiMessage>> LoadHistoryAsync(QueueItem item, Turn turn, int max, CancellationToken ct)
    {
        await using var db = new AppDbContext(dbOptions);
        var keep = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == item.ConversationId && (m.QueueItemId == item.Id || m.Id == turn.AnchorId))
            .ToListAsync(ct);
        var keepIds = keep.Select(m => m.Id).ToArray();

        // Baris tiket selalu se-flag, jadi tiket utama selalu membawa hasil tool-nya; tiket alternatif tidak pernah
        // memasukkan jawaban alternatif lain (maupun jawaban utamanya sendiri) ke konteksnya.
        var budget = Math.Max(0, max - keep.Count);
        var recent = budget == 0
            ? new List<Message>()
            : await db.Messages.AsNoTracking()
                .Where(m => m.ConversationId == item.ConversationId && m.IsPrimary && m.Id <= turn.BoundaryId && !keepIds.Contains(m.Id))
                .OrderByDescending(m => m.Id).Take(budget).ToListAsync(ct);

        var rows = keep.Concat(recent).OrderBy(m => m.Id).ToList();
        var attachmentIds = rows.Where(m => !string.IsNullOrEmpty(m.AttachmentIdsJson))
            .SelectMany(m => JsonSerializer.Deserialize<Guid[]>(m.AttachmentIdsJson!) ?? []).Distinct().ToList();
        var attachments = attachmentIds.Count == 0
            ? new Dictionary<Guid, Attachment>()
            : await db.Attachments.AsNoTracking().Where(a => attachmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        // Batas sudah dipakai saat memilih baris; Build tidak boleh memotong lagi supaya jangkar dan baris tiket sendiri utuh.
        var history = ConversationBuilder.Build(rows, attachments, rows.Count);
        if (history.Count == 0 || history[^1].Role is not (MessageRoles.User or "tool"))
            throw new InvalidOperationException("Riwayat tidak berakhir pada pesan user atau hasil tool");
        return history;
    }

    private async Task RunToolsAsync(QueueItem item, ExecutionPolicy policy, long anchorId, string model, AiResponse response,
        CancellationToken ct, Guid? streamId)
    {
        Message assistantRow;
        var jobs = new List<JobRef>();

        await using (var db = new AppDbContext(dbOptions))
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Guard kepemilikan di dalam transaksi penulisan: baris assistant, job, dan hasil tool tidak boleh ditulis
            // run yang kuncinya sudah dipulihkan (lease bisa saja belum sempat mendeteksinya).
            var owned = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE queue_items SET locked_at = SYSUTCDATETIME() WHERE id = {item.Id} AND status = {Statuses.Processing} AND locked_by = {run.RunId}", ct);
            if (owned != 1) throw new LostTicketException($"Tiket {item.Id} bukan lagi milik run {run.RunId}");

            var text = string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim();
            assistantRow = new Message
            {
                ConversationId = item.ConversationId, Role = MessageRoles.Assistant, Content = text, IsVisible = text is not null,
                AiContentJson = new JsonArray(response.Message.ToJson()).ToJsonString(), QueueItemId = item.Id, ModelAlias = model,
                StreamId = streamId, IsPrimary = item.IsPrimary,
            };
            db.Messages.Add(assistantRow);
            await db.SaveChangesAsync(ct);

            var results = new JsonArray();
            foreach (var call in response.ToolCalls)
            {
                var result = await ToolDispatcher.ExecuteAsync(new ToolContext(db, item.ConversationId, policy, anchorId, model,
                    ModelSnapshot.SupportsStreaming(item.PayloadJson), ModelSnapshot.SupportsVision(item.PayloadJson), item.IsPrimary,
                    ReasoningEffort: ModelSnapshot.ReasoningEffort(item.PayloadJson), WebSearch: webSearch),
                    call.Name, call.Arguments, ct);
                results.Add(new AiMessage("tool", result.IsError ? "ERROR: " + result.Text : result.Text, ToolCallId: call.Id).ToJson());
                jobs.AddRange((result.NewJobIds ?? []).Select(id => new JobRef(item.ConversationId, id)));
            }

            db.Messages.Add(new Message
            {
                ConversationId = item.ConversationId, Role = MessageRoles.User, IsVisible = false, ModelAlias = model,
                AiContentJson = results.ToJsonString(), QueueItemId = item.Id, IsPrimary = item.IsPrimary,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (assistantRow.IsVisible)
            await notify.NotifyMessagesAsync([new MessageRef(item.ConversationId, assistantRow.Id)], ct);
        await notify.NotifyJobsAsync(jobs, ct);
    }

    private async Task CompleteAsync(QueueItem item, string text, string? attachmentIdsJson, string? finishReason, CancellationToken ct,
        ReplyTiming timing, Guid? streamId = null)
    {
        var commit = Stopwatch.StartNew();
        var reply = await repo.CompleteQueueItemWithReplyAsync(item.Id, run.RunId, item.ConversationId, text, attachmentIdsJson, finishReason, ct, streamId, timing);
        if (reply is { } written)
            await notify.NotifyMessagesAsync([written], ct);
        else
            throw new LostTicketException($"Tiket {item.Id} bukan lagi milik run {run.RunId}");
        logger.LogInformation("Timing: commit balasan + notify={Ms}ms", commit.ElapsedMilliseconds);
    }

    /// <summary>
    /// Waktu tunggu antrean dari baris hasil klaim (heartbeat menulis ulang <c>locked_at</c> setelahnya). Nilai negatif
    /// atau di luar int tidak dilaporkan.
    /// </summary>
    private static int? QueueWaitMs(QueueItem item)
    {
        if (item.LockedAt is not { } locked) return null;
        var ms = (locked - item.CreatedAt).TotalMilliseconds;
        return ms is >= 0 and <= int.MaxValue ? (int)ms : null;
    }
}
