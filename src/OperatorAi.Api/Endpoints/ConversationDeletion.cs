using Microsoft.EntityFrameworkCore;
using OperatorAi.Api.Auth;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Queue;
using OperatorAi.Shared.Storage;

namespace OperatorAi.Api;

/// <summary>
/// Hapus sesi permanen (§4.1): satu transaksi mengunci baris percakapan, menolak selama masih ada tiket/job berjalan,
/// lalu menghapus pesan/lampiran/tiket/job. Jawaban koleksi tetap ada sebagai snapshot; hanya referensinya dilepas.
/// File lampiran baru dihapus setelah commit, dan kegagalan file tidak pernah menggagalkan 204.
/// </summary>
public static class ConversationDeletion
{
    public const string BusyCode = "conversation_busy";
    public const string BusyMessage = "Sesi masih memproses jawaban. Coba lagi setelah selesai.";

    /// <param name="AttachmentPaths">Path relatif lampiran yang barisnya sudah terhapus; file-nya dibersihkan pemanggil.</param>
    public sealed record DeleteOutcome(int Status, IReadOnlyList<string> AttachmentPaths);

    public static async Task<IResult> HandleAsync(Guid id, HttpContext http, AppDbContext db, FileStorage storage,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var outcome = await DeleteAsync(db, id, http.User.UserId(), ct);
        if (outcome.Status == StatusCodes.Status409Conflict)
            return Results.Json(new { error = BusyMessage, code = BusyCode }, statusCode: StatusCodes.Status409Conflict);
        if (outcome.Status != StatusCodes.Status204NoContent)
            return Results.NotFound();

        // Setelah commit: file boleh hilang/terkunci, yang penting barisnya sudah tidak ada.
        RemoveFiles(storage, loggerFactory.CreateLogger(nameof(ConversationDeletion)), id, outcome.AttachmentPaths);
        return Results.NoContent();
    }

    /// <summary>
    /// 204 saat baris percakapan (dan seluruh isinya) terhapus, 404 saat bukan milik user, 409 saat masih ada
    /// tiket/job berjalan. Kepemilikan dan kesibukan diperiksa di dalam transaksi yang sama dengan penghapusan.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteAsync(AppDbContext db, Guid conversationId, string userId,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Baris percakapan dikunci lebih dulu: hapus kedua untuk sesi yang sama menunggu di sini, bukan menebak
        // dari pemeriksaan di luar transaksi (bisa basi saat worker menulis baris baru).
        var owner = await db.Database.SqlQuery<string>(
            $"SELECT user_id AS Value FROM conversations WITH (UPDLOCK, HOLDLOCK) WHERE id = {conversationId}")
            .ToListAsync(ct);
        if (owner.Count == 0 || owner[0] != userId) return new(StatusCodes.Status404NotFound, []);
        if (await BusyAsync(db, conversationId, ct)) return new(StatusCodes.Status409Conflict, []);

        // Path dibaca sebelum barisnya hilang; tidak ada isi lain yang dibutuhkan setelah commit.
        var paths = await db.Database.SqlQuery<string>(
            $"SELECT storage_path AS Value FROM attachments WHERE conversation_id = {conversationId}").ToListAsync(ct);

        // Dependensi antar job dilepas dulu: FK self-referencing NoAction bisa gagal kalau urutan hapus job tidak menguntungkan.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE jobs SET depends_on_job_id = NULL WHERE conversation_id = {conversationId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM jobs WHERE conversation_id = {conversationId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM queue_items WHERE conversation_id = {conversationId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM messages WHERE conversation_id = {conversationId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM attachments WHERE conversation_id = {conversationId}", ct);
        // Snapshot jawaban koleksi tidak ikut terhapus; referensi ke sesi/pesan yang hilang dilepas manual (tanpa FK).
        await db.Database.ExecuteSqlAsync(
            $"UPDATE saved_answers SET conversation_id = NULL, message_id = NULL WHERE conversation_id = {conversationId}", ct);
        // Versi artifact ikut terhapus lewat FK cascade.
        await db.Database.ExecuteSqlAsync($"DELETE FROM artifacts WHERE conversation_id = {conversationId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM conversations WHERE id = {conversationId}", ct);
        await tx.CommitAsync(ct);
        return new(StatusCodes.Status204NoContent, paths);
    }

    /// <summary>Tiket atau job percakapan yang belum selesai (status selain Done/Failed).</summary>
    private static async Task<bool> BusyAsync(AppDbContext db, Guid conversationId, CancellationToken ct)
    {
        var busy = await db.Database.SqlQuery<int>($"""
            SELECT 1 AS Value FROM queue_items
            WHERE conversation_id = {conversationId} AND status NOT IN ({Statuses.Done}, {Statuses.Failed})
            UNION
            SELECT 1 FROM jobs
            WHERE conversation_id = {conversationId} AND status NOT IN ({Statuses.Done}, {Statuses.Failed})
            """).ToListAsync(ct);
        return busy.Count > 0;
    }

    /// <summary>
    /// Hapus file lampiran dan direktori sesi (bila kosong) setelah commit. File yang sudah tidak ada bukan masalah;
    /// kegagalan lain hanya dicatat, karena barisnya sudah terhapus dan 204 tidak boleh berubah jadi galat.
    /// </summary>
    public static void RemoveFiles(FileStorage storage, ILogger logger, Guid conversationId,
        IReadOnlyList<string> storagePaths)
    {
        foreach (var path in storagePaths)
        {
            try
            {
                var full = storage.Resolve(path);
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Gagal menghapus lampiran sesi {ConversationId}: {Path}", conversationId, path);
            }
        }

        foreach (var kind in new[] { "uploads", "outputs" })
        {
            try
            {
                var directory = storage.Resolve($"{kind}/{conversationId}");
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Gagal menghapus direktori {Kind} sesi {ConversationId}.", kind, conversationId);
            }
        }
    }
}
