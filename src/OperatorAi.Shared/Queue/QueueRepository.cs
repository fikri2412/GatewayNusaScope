using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Contracts;

namespace OperatorAi.Shared.Queue;

/// <summary>
/// Klaim, selesai, gagal + retry, dan recovery untuk <c>queue_items</c> dan <c>jobs</c> (DESIGN bagian 6).
/// Setiap metode membuka koneksinya sendiri; tiap proses worker hanya memakainya beberapa kali.
/// Penyelesaian dan kegagalan selalu dijaga <c>locked_by = runId</c> supaya worker yang kunci-nya sudah
/// dipulihkan (lalu diklaim proses lain) tidak menimpa hasil proses baru.
/// </summary>
public sealed class QueueRepository
{
    static QueueRepository() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    // Syarat klaim dipakai bersama oleh klaim dan hitungan dispatcher supaya keduanya tidak pernah berbeda;
    // bila beda, dispatcher menyalakan proses yang tidak kebagian apa pun.
    private static readonly string QueueEligible = $"""
        q.status = '{Statuses.Pending}'
        AND NOT EXISTS (SELECT 1 FROM queue_items o
                        WHERE o.conversation_id = q.conversation_id
                          AND (o.status = '{Statuses.Processing}' OR (o.status = '{Statuses.Pending}' AND o.id < q.id)))
        """;

    private static readonly string JobEligible = $"""
        j.status = '{Statuses.Pending}'
        AND j.job_type IN @types
        AND (j.depends_on_job_id IS NULL
             OR EXISTS (SELECT 1 FROM jobs d WHERE d.id = j.depends_on_job_id AND d.status = '{Statuses.Done}'))
        """;

    private static readonly string ClaimQueueSql = $"""
        WITH next AS (
            SELECT TOP (1) q.* FROM queue_items q WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE {QueueEligible}
            ORDER BY q.id
        )
        UPDATE next SET status = '{Statuses.Processing}', locked_at = SYSUTCDATETIME(), locked_by = @runId
        OUTPUT inserted.*;
        """;

    private static readonly string ClaimJobSql = $"""
        WITH next AS (
            SELECT TOP (1) j.* FROM jobs j WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE {JobEligible}
            ORDER BY j.id
        )
        UPDATE next SET status = '{Statuses.Processing}', locked_at = SYSUTCDATETIME(), locked_by = @runId
        OUTPUT inserted.*;
        """;

    // Masih ada sisa retry menurut snapshot baris (bukan setting terbaru).
    private const string RetryLeft =
        "retry_count + 1 < CONVERT(int, JSON_VALUE(execution_policy_json, '$.maxRetries'))";

    // Kegagalan: retry_count naik; masih di bawah batas → Pending lagi, selain itu Failed. Semua ekspresi SET
    // membaca nilai lama baris, jadi retry_count + 1 di tiap CASE konsisten. @terminal (mis. model AI dicabut
    // gateway) menuju Failed tanpa menunggu batas retry, tanpa memalsukan retry_count: percobaan yang benar-benar
    // terjadi tetap tercatat.
    private static string FailSet(string table) => $"""
        UPDATE {table} SET
            retry_count = retry_count + 1,
            status = CASE WHEN @terminal = 0 AND {RetryLeft} THEN '{Statuses.Pending}' ELSE '{Statuses.Failed}' END,
            error = @error,
            completed_at = CASE WHEN @terminal = 0 AND {RetryLeft} THEN NULL ELSE SYSUTCDATETIME() END,
            locked_at = NULL, locked_by = NULL
        """;

    private static readonly string FailQueueSql = FailSet("queue_items") + """

        OUTPUT inserted.id, inserted.conversation_id, inserted.type, inserted.status, inserted.payload_json,
               inserted.is_primary
        """;

    private static readonly string DoneQueueSql = $"""
        UPDATE queue_items SET status = '{Statuses.Done}', completed_at = SYSUTCDATETIME()
        OUTPUT inserted.payload_json, inserted.is_primary
        WHERE id = @id AND status = '{Statuses.Processing}' AND locked_by = @runId
        """;

    private static readonly string FailJobSql = FailSet("jobs") + """

        OUTPUT inserted.id, inserted.conversation_id, inserted.job_type, inserted.status, inserted.error,
               inserted.execution_policy_json, inserted.source_message_id, inserted.input_json, inserted.is_primary
        """;

    private static readonly string CascadeSql = $"""
        WITH d AS (
            SELECT id FROM jobs WHERE depends_on_job_id = @id
            UNION ALL
            SELECT j.id FROM jobs j JOIN d ON j.depends_on_job_id = d.id
        )
        UPDATE jobs SET status = '{Statuses.Failed}', error = 'Dependency failed',
                        completed_at = SYSUTCDATETIME(), locked_at = NULL, locked_by = NULL
        OUTPUT inserted.id, inserted.conversation_id
        WHERE id IN (SELECT id FROM d) AND status = '{Statuses.Pending}';
        """;

    private const string InsertQueueItemSql =
        "INSERT INTO queue_items (conversation_id, type, payload_json, status, execution_policy_json, source_message_id, is_primary) " +
        "VALUES (@conversationId, @type, @payload, @status, @executionPolicyJson, @sourceMessageId, @isPrimary)";

    // Model baris balasan selalu berasal dari snapshot baris tiket/job yang menulisnya (bukan percakapan yang
    // modelnya bisa sudah berganti). Timing dan flag hanya diisi jalur penyelesaian tiket; total_ms dihitung dari
    // created_at baris tiket di dalam INSERT supaya jam aplikasi tidak pernah jadi sumbernya.
    private const string InsertMessageSql =
        "INSERT INTO messages (conversation_id, role, content, is_visible, attachment_ids_json, queue_item_id, finish_reason, model_alias, stream_id, " +
        "is_primary, tokens_used, queue_wait_ms, first_token_ms, total_ms) " +
        "OUTPUT inserted.id SELECT @conversationId, @role, @content, 1, @attachmentIds, @queueItemId, @finishReason, @modelAlias, @streamId, " +
        "@isPrimary, @tokensUsed, @queueWaitMs, @firstTokenMs, " +
        "CASE WHEN @timed = 1 THEN TRY_CAST(DATEDIFF_BIG(MILLISECOND, " +
        "(SELECT created_at FROM queue_items WHERE id = @queueItemId), SYSUTCDATETIME()) AS int) END";

    private readonly string _connectionString;

    public QueueRepository(string connectionString) => _connectionString = connectionString;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ---- Klaim ----

    public async Task<QueueItem?> ClaimQueueItemAsync(string runId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<QueueItem>(
            new CommandDefinition(ClaimQueueSql, new { runId }, cancellationToken: ct));
    }

    public async Task<Job?> ClaimJobAsync(string runId, IReadOnlyCollection<string> types, CancellationToken ct = default)
    {
        if (types.Count == 0) return null;
        await using var conn = await OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Job>(
            new CommandDefinition(ClaimJobSql, new { runId, types }, cancellationToken: ct));
    }

    /// <summary>Jumlah item yang bisa diklaim sekarang (dipakai dispatcher).</summary>
    public async Task<int> CountEligibleQueueItemsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition($"SELECT COUNT(*) FROM queue_items q WHERE {QueueEligible}", cancellationToken: ct));
    }

    /// <summary>
    /// Berapa dari <paramref name="runIds"/> yang sudah memegang item/job. Proses yang hidup tetapi belum
    /// mengklaim (masih cold start) akan mengambil tiket yang sama dengan hitungan "memenuhi syarat";
    /// dispatcher mengurangkannya supaya tidak menyalakan proses yang pasti tidak kebagian.
    /// </summary>
    public async Task<int> CountHeldQueueItemsAsync(IReadOnlyCollection<string> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0) return 0;
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM queue_items WHERE status = '{Statuses.Processing}' AND locked_by IN @runIds",
            new { runIds }, cancellationToken: ct));
    }

    public async Task<int> CountHeldJobsAsync(IReadOnlyCollection<string> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0) return 0;
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM jobs WHERE status = '{Statuses.Processing}' AND locked_by IN @runIds",
            new { runIds }, cancellationToken: ct));
    }

    public async Task<int> CountEligibleJobsAsync(IReadOnlyCollection<string> types, CancellationToken ct = default)
    {
        if (types.Count == 0) return 0;
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition($"SELECT COUNT(*) FROM jobs j WHERE {JobEligible}", new { types }, cancellationToken: ct));
    }
    /// <summary>Guarded heartbeat: klaim yang hilang membatalkan pekerjaan sebelum side effect berikutnya.</summary>
    public async Task<TicketLease> KeepAliveAsync(long id, string runId, bool isJob, CancellationToken ct)
    {
        try
        {
            if (!await HeartbeatAsync(id, runId, isJob, ct)) throw new LeaseLostException();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not LeaseLostException)
        {
            throw new LeaseLostException(ex);
        }
        return new TicketLease(token => HeartbeatAsync(id, runId, isJob, token), ct);
    }

    private async Task<bool> HeartbeatAsync(long id, string runId, bool isJob, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        ct = deadline.Token;
        await using var conn = await OpenAsync(ct);
        var table = isJob ? "jobs" : "queue_items";
        try
        {
            // LOCK_TIMEOUT: putaran tool TicketProcessor memegang kunci X atas baris ini sampai commit (guard kepemilikan
            // di transaksinya). Heartbeat yang menunggu kunci itu berarti baris masih dipegang, bukan hilang, jadi
            // tidak boleh menunggu sampai deadline lalu membatalkan tiket yang sehat.
            return await conn.ExecuteAsync(new CommandDefinition(
                $"SET LOCK_TIMEOUT 2000; UPDATE {table} SET locked_at = SYSUTCDATETIME() WHERE id = @id AND status = '{Statuses.Processing}' AND locked_by = @runId",
                new { id, runId }, cancellationToken: ct)) == 1;
        }
        catch (SqlException ex) when (ex.Number == 1222)
        {
            return true; // lock-timeout: kehilangan tidak terbukti; detak berikutnya memeriksa lagi
        }
    }

    /// <summary>Satu statement snapshot untuk report berkala; kegagalan dibawa ke caller, bukan hitungan nol palsu.</summary>
    public async Task<QueueRuntimeSnapshot> ReadRuntimeSnapshotAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var rows = (await conn.QueryAsync<RuntimeCount>(new CommandDefinition("""
            WITH work AS (
                SELECT 'queue' AS kind, status, execution_policy_json FROM queue_items
                UNION ALL SELECT 'job', status, execution_policy_json FROM jobs
            )
            SELECT kind, status,
                   CASE WHEN status IN ('Pending','Processing') THEN JSON_VALUE(execution_policy_json, '$.policyHash') END AS policy_hash,
                   COUNT_BIG(*) AS count
            FROM work
            GROUP BY kind, status,
                     CASE WHEN status IN ('Pending','Processing') THEN JSON_VALUE(execution_policy_json, '$.policyHash') END
            """, cancellationToken: ct))).ToList();
        StatusCounts Counts(string kind) => new(
            rows.Where(r => r.Kind == kind && r.Status == Statuses.Pending).Sum(r => r.Count),
            rows.Where(r => r.Kind == kind && r.Status == Statuses.Processing).Sum(r => r.Count),
            rows.Where(r => r.Kind == kind && r.Status == Statuses.Done).Sum(r => r.Count),
            rows.Where(r => r.Kind == kind && r.Status == Statuses.Failed).Sum(r => r.Count));
        var active = rows.Where(r => r.Status is Statuses.Pending or Statuses.Processing)
            .GroupBy(r => r.PolicyHash ?? throw new InvalidDataException("Pekerjaan aktif tanpa policy hash."))
            .Select(g => new ActivePolicyCount(g.Key, g.Where(r => r.Kind == "queue").Sum(r => r.Count),
                g.Where(r => r.Kind == "job").Sum(r => r.Count))).ToArray();
        return new QueueRuntimeSnapshot(Counts("queue"), Counts("job"), active);
    }

    private sealed class RuntimeCount
    {
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "";
        public string? PolicyHash { get; set; }
        public long Count { get; set; }
    }

    // ---- Selesai ----

    /// <returns>false bila item bukan lagi milik <paramref name="runId"/> (sudah dipulihkan proses lain).</returns>
    public async Task<bool> CompleteQueueItemAsync(long id, string runId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SnapshotRow>(
            new CommandDefinition(DoneQueueSql, new { id, runId }, cancellationToken: ct)) is not null;
    }

    /// <summary>
    /// Done + balasan assistant dalam satu transaksi: crash di antara keduanya tidak bisa menggandakan balasan saat retry.
    /// </summary>
    /// <param name="timing">Statistik tiket yang ditulis pada baris balasan; null untuk baris tanpa pengukuran (mis. permintaan maaf).</param>
    /// <returns>Pesan balasan, atau null bila item bukan lagi milik <paramref name="runId"/> (tidak ada yang ditulis).</returns>
    public async Task<MessageRef?> CompleteQueueItemWithReplyAsync(long id, string runId, Guid conversationId, string reply,
        string? attachmentIdsJson = null, string? finishReason = null, CancellationToken ct = default, Guid? streamId = null,
        ReplyTiming? timing = null)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var done = await conn.QueryFirstOrDefaultAsync<SnapshotRow>(
            new CommandDefinition(DoneQueueSql, new { id, runId }, tx, cancellationToken: ct));
        if (done is null) return null;
        var messageId = await InsertAssistantMessageAsync(conn, tx, conversationId, reply, attachmentIdsJson, id, finishReason,
            SnapshotModel(done.PayloadJson), done.IsPrimary, ct, streamId, timing);
        await tx.CommitAsync(ct);
        return new MessageRef(conversationId, messageId);
    }

    /// <summary>Set Done + output; bila <c>notify_operator = 1</c>, insert <c>JobCompleted</c> dalam transaksi yang sama.</summary>
    public async Task<bool> CompleteJobAsync(long id, string runId, string? outputJson, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var done = await conn.QueryFirstOrDefaultAsync<CompletedJobRow>(new CommandDefinition(
            $"""
            UPDATE jobs SET status = '{Statuses.Done}', output_json = @outputJson, completed_at = SYSUTCDATETIME()
            OUTPUT inserted.conversation_id, inserted.job_type, inserted.notify_operator,
                   inserted.execution_policy_json, inserted.source_message_id, inserted.input_json, inserted.is_primary
            WHERE id = @id AND status = '{Statuses.Processing}' AND locked_by = @runId
            """, new { id, runId, outputJson }, tx, cancellationToken: ct));
        if (done is null) return false;
        if (done.NotifyOperator)
            await InsertQueueItemAsync(conn, tx, done.ConversationId, QueueTypes.JobCompleted,
                JsonSerializer.Serialize(new { jobId = id, model = SnapshotModel(done.InputJson), jobType = done.JobType,
                    supportsStreaming = ModelSnapshot.SupportsStreaming(done.InputJson),
                    supportsVision = ModelSnapshot.SupportsVision(done.InputJson),
                    reasoningEffort = ModelSnapshot.ReasoningEffort(done.InputJson) }),
                done.ExecutionPolicyJson, done.SourceMessageId, done.IsPrimary, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---- Gagal + retry ----

    /// <summary>
    /// Retry masih tersisa → Pending; habis → Failed, dan bila itu pesan user, satu permintaan maaf visible ditulis dalam
    /// transaksi yang sama (tanpa itu percakapan menggantung). Null bila item bukan lagi milik <paramref name="runId"/>.
    /// </summary>
    public Task<FailResult?> FailQueueItemAsync(long id, string runId, string error, CancellationToken ct = default) =>
        FailQueueItemOnceAsync(id, runId, error, ct, terminal: false, Replies.FailureApology);

    /// <summary>
    /// Kegagalan yang tidak boleh dicoba lagi (mis. model dicabut gateway): status langsung Failed walau sisa retry
    /// masih ada, dan percakapan tidak boleh menggantung — <paramref name="publicReply"/> ditulis sebagai balasan
    /// visible untuk tiket ini, termasuk tiket callback job. Null bila item bukan lagi milik <paramref name="runId"/>.
    /// </summary>
    public Task<FailResult?> FailQueueItemTerminallyAsync(long id, string runId, string error, string publicReply,
        CancellationToken ct = default) =>
        FailQueueItemOnceAsync(id, runId, error, ct, terminal: true, publicReply);

    private async Task<FailResult?> FailQueueItemOnceAsync(long id, string runId, string error, CancellationToken ct,
        bool terminal, string publicReply)
    {
        var (rows, replies) = await FailQueueItemsAsync(
            $"WHERE id = @id AND status = '{Statuses.Processing}' AND locked_by = @runId",
            new { id, runId, error, terminal }, ct, terminal, publicReply);
        return rows.Count == 0 ? null : new FailResult(rows[0].Status, replies.Count > 0 ? replies[0] : null);
    }

    /// <summary>
    /// Gagal permanen (retry habis): semua job yang bergantung padanya (rekursif) ikut Failed
    /// ("Dependency failed") dan satu <c>JobFailed</c> dimasukkan ke queue untuk operator.
    /// </summary>
    public Task<FailJobResult?> FailJobAsync(long id, string runId, string error, CancellationToken ct = default) =>
        FailJobOnceAsync(id, runId, error, ct, terminal: false);

    /// <summary>Kegagalan permanen: hentikan rantai dan simpan kategori server supaya callback tidak menebak penyebab.</summary>
    public Task<FailJobResult?> FailJobTerminallyAsync(long id, string runId, string error, CancellationToken ct = default,
        string category = FailureCategories.ModelUnavailable) =>
        FailJobOnceAsync(id, runId, error, ct, terminal: true, category);

    private async Task<FailJobResult?> FailJobOnceAsync(long id, string runId, string error, CancellationToken ct, bool terminal,
        string? category = null)
    {
        var (rows, updates) = await FailJobsAsync($"WHERE id = @id AND status = '{Statuses.Processing}' AND locked_by = @runId",
            new { id, runId, error, terminal }, ct, terminal, category);
        return rows.Count == 0 ? null : new FailJobResult(rows[0].Status, updates);
    }

    // ---- Recovery ----

    /// <summary>
    /// Item/job Processing yang heartbeat-nya lebih tua dari StaleLockMinutes snapshot baris diperlakukan sebagai
    /// gagal (retry_count naik; Pending lagi selama belum melewati batas snapshot). Dipanggil dispatcher tiap sweep
    /// dan awal setiap worker; perubahan setting tidak mempersingkat lease pekerjaan yang sedang berjalan.
    /// </summary>
    public Task<RecoveryResult> RecoverStaleAsync(CancellationToken ct = default) =>
        RecoverAsync($"status = '{Statuses.Processing}' AND locked_at < DATEADD(MINUTE, -CONVERT(int, JSON_VALUE(execution_policy_json, '$.staleLockMinutes')), SYSUTCDATETIME())",
            new { error = "Heartbeat worker melewati batas snapshot pekerjaan", terminal = false }, ct);

    /// <summary>Proses anak <paramref name="runId"/> crash: pulihkan tiketnya sekarang, tanpa menunggu StaleLockMinutes.</summary>
    public Task<RecoveryResult> RecoverRunAsync(string runId, CancellationToken ct = default) =>
        RecoverAsync($"status = '{Statuses.Processing}' AND locked_by = @runId",
            new { runId, error = "Proses worker berhenti tak terduga", terminal = false }, ct);

    /// <summary>
    /// Worker dihentikan baik-baik (SIGINT/SIGTERM) sebelum tiketnya selesai: kembalikan tiket run ini ke Pending tanpa
    /// menaikkan <c>retry_count</c> (berhenti karena host, bukan kesalahan tiket) supaya proses lain langsung mengambilnya.
    /// </summary>
    /// <returns>Jumlah tiket (queue item + job) yang dikembalikan.</returns>
    public async Task<int> ReleaseRunAsync(string runId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE queue_items SET status = '{Statuses.Pending}', locked_at = NULL, locked_by = NULL
            WHERE status = '{Statuses.Processing}' AND locked_by = @runId;
            UPDATE jobs SET status = '{Statuses.Pending}', locked_at = NULL, locked_by = NULL
            WHERE status = '{Statuses.Processing}' AND locked_by = @runId;
            """, new { runId }, cancellationToken: ct));
    }

    private async Task<RecoveryResult> RecoverAsync(string where, object args, CancellationToken ct)
    {
        var (queueRows, apologies) = await FailQueueItemsAsync("WHERE " + where, args, ct, terminal: false, Replies.FailureApology);
        var (jobRows, jobUpdates) = await FailJobsAsync("WHERE " + where, args, ct, terminal: false);
        return new RecoveryResult(queueRows.Count + jobRows.Count, apologies, jobUpdates,
            queueRows.Select(q => new QueueRef(q.ConversationId, q.Id)).ToList());
    }

    private async Task<(List<FailedQueueRow> Rows, List<MessageRef> Replies)> FailQueueItemsAsync(
        string where, object args, CancellationToken ct, bool terminal, string publicReply)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var rows = (await conn.QueryAsync<FailedQueueRow>(
            new CommandDefinition(FailQueueSql + "\n" + where, args, tx, cancellationToken: ct))).ToList();
        var replies = new List<MessageRef>();
        // Tiket callback job tidak pernah punya pesan user sendiri, jadi kegagalan permanen harus tetap menjawab;
        // retry yang masih tersisa cukup menunggu percobaan berikutnya.
        foreach (var r in rows.Where(r => r.Status == Statuses.Failed && (terminal || r.Type == QueueTypes.UserMessage)))
            replies.Add(new MessageRef(r.ConversationId,
                await InsertAssistantMessageAsync(conn, tx, r.ConversationId, publicReply, null, r.Id, null, SnapshotModel(r.PayloadJson),
                    r.IsPrimary, ct)));
        await tx.CommitAsync(ct);
        return (rows, replies);
    }

    private async Task<(List<FailedJobRow> Rows, List<JobRef> Updates)> FailJobsAsync(string where, object args, CancellationToken ct,
        bool terminal, string? category = null)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var rows = (await conn.QueryAsync<FailedJobRow>(
            new CommandDefinition(FailJobSql + "\n" + where, args, tx, cancellationToken: ct))).ToList();
        var updates = rows.Select(r => new JobRef(r.ConversationId, r.Id)).ToList();
        foreach (var f in rows.Where(r => r.Status == Statuses.Failed))
        {
            var cascaded = await conn.QueryAsync<CascadedJobRow>(new CommandDefinition(CascadeSql, new { f.Id }, tx, cancellationToken: ct));
            updates.AddRange(cascaded.Select(c => new JobRef(c.ConversationId, c.Id)));
            await InsertQueueItemAsync(conn, tx, f.ConversationId, QueueTypes.JobFailed,
                JsonSerializer.Serialize(new
                {
                    jobId = f.Id, model = SnapshotModel(f.InputJson), jobType = f.JobType, error = f.Error,
                    supportsStreaming = ModelSnapshot.SupportsStreaming(f.InputJson),
                    supportsVision = ModelSnapshot.SupportsVision(f.InputJson),
                    reasoningEffort = ModelSnapshot.ReasoningEffort(f.InputJson),
                    category = terminal ? category ?? FailureCategories.ModelUnavailable : null,
                }), f.ExecutionPolicyJson, f.SourceMessageId, f.IsPrimary, ct);
        }
        await tx.CommitAsync(ct);
        return (rows, updates);
    }

    /// <summary>Model workflow dari snapshot baris (payload tiket / input job); null bila baris lama belum punya.</summary>
    private static string? SnapshotModel(string? snapshotJson)
    {
        try
        {
            return snapshotJson is null ? null : ModelSnapshot.Read(snapshotJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static Task InsertQueueItemAsync(SqlConnection conn, SqlTransaction tx, Guid conversationId,
        string type, string payload, string? executionPolicyJson, long? sourceMessageId, bool isPrimary, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(InsertQueueItemSql,
            new { conversationId, type, payload, status = Statuses.Pending, executionPolicyJson, sourceMessageId, isPrimary }, tx, cancellationToken: ct));

    private static Task<long> InsertAssistantMessageAsync(SqlConnection conn, SqlTransaction tx, Guid conversationId,
        string content, string? attachmentIds, long queueItemId, string? finishReason, string? modelAlias, bool isPrimary,
        CancellationToken ct, Guid? streamId = null, ReplyTiming? timing = null) =>
        conn.ExecuteScalarAsync<long>(new CommandDefinition(InsertMessageSql,
            new { conversationId, role = MessageRoles.Assistant, content, attachmentIds, queueItemId, finishReason, modelAlias, streamId,
                  isPrimary, tokensUsed = timing?.TokensUsed, queueWaitMs = timing?.QueueWaitMs, firstTokenMs = timing?.FirstTokenMs,
                  timed = timing is not null }, tx, cancellationToken: ct));

    private sealed class SnapshotRow
    {
        public string? PayloadJson { get; set; }
        public bool IsPrimary { get; set; }
    }

    private sealed class FailedJobRow
    {
        public long Id { get; set; }
        public Guid ConversationId { get; set; }
        public string JobType { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string? ExecutionPolicyJson { get; set; }
        public long? SourceMessageId { get; set; }
        public string? InputJson { get; set; }
        public bool IsPrimary { get; set; }
    }

    private sealed class CompletedJobRow
    {
        public Guid ConversationId { get; set; }
        public string JobType { get; set; } = "";
        public bool NotifyOperator { get; set; }
        public string? ExecutionPolicyJson { get; set; }
        public long? SourceMessageId { get; set; }
        public string? InputJson { get; set; }
        public bool IsPrimary { get; set; }
    }

    private sealed class FailedQueueRow
    {
        public long Id { get; set; }
        public Guid ConversationId { get; set; }
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        public string? PayloadJson { get; set; }
        public bool IsPrimary { get; set; }
    }

    private sealed class CascadedJobRow
    {
        public long Id { get; set; }
        public Guid ConversationId { get; set; }
    }
}
