namespace OperatorAi.Shared.Queue;

/// <summary>Pesan yang baru ditulis repository; pemanggil yang mendorongnya ke client (notify / SignalR).</summary>
public readonly record struct MessageRef(Guid ConversationId, long MessageId);

/// <summary>Job yang statusnya baru berubah di repository (gagal, Pending lagi, atau ikut gagal karena dependensi).</summary>
public readonly record struct JobRef(Guid ConversationId, long JobId);

public readonly record struct QueueRef(Guid ConversationId, long QueueItemId);

/// <summary>
/// Statistik satu tiket untuk baris balasan akhirnya. Null berarti tidak terukur: balasan langsung tanpa panggilan AI
/// tidak punya token, jalur non-streaming tidak punya token pertama, nilai negatif/di luar int tidak dipakai.
/// <c>TotalMs</c> dihitung di dalam INSERT dari <c>queue_items.created_at</c>, jadi tidak ikut di sini.
/// </summary>
public readonly record struct ReplyTiming(int? TokensUsed, int? QueueWaitMs, int? FirstTokenMs);

/// <param name="Status">Status baru: Pending (akan dicoba lagi) atau Failed (retry habis).</param>
/// <param name="Apology">Pesan permintaan maaf bila pesan user gagal permanen.</param>
public sealed record FailResult(string Status, MessageRef? Apology);

/// <param name="Status">Status baru job: Pending (akan dicoba lagi) atau Failed (retry habis).</param>
/// <param name="Updated">Job yang berubah: job itu sendiri dan, bila gagal permanen, semua yang bergantung padanya.</param>
public sealed record FailJobResult(string Status, IReadOnlyList<JobRef> Updated);

/// <param name="Recovered">Item queue + job yang dipulihkan (Pending lagi atau Failed).</param>
/// <param name="Apologies">Permintaan maaf untuk pesan user yang gagal permanen saat recovery.</param>
/// <param name="JobUpdates">Job yang berubah status saat recovery (untuk dorong <c>jobUpdated</c>).</param>
public sealed record RecoveryResult(int Recovered, IReadOnlyList<MessageRef> Apologies, IReadOnlyList<JobRef> JobUpdates,
    IReadOnlyList<QueueRef> QueueUpdates);

public static class Replies
{
    public const string FailureApology = "Maaf, sedang ada gangguan. Silakan coba lagi sebentar lagi.";

    /// <summary>
    /// Model yang dipilih dicabut/tidak lagi diizinkan gateway: tidak ada percobaan ulang, jadi user harus tahu
    /// kenapa dan apa langkah berikutnya.
    /// </summary>
    public const string ModelUnavailable =
        "Model AI yang dipilih sudah tidak tersedia. Silakan pilih model lain, lalu kirim ulang pesan Anda.";

    /// <summary>Penyedia AI tidak merespons tepat waktu (<c>Ai:StreamFirstByteSeconds</c>); tiket dihentikan tanpa retry.</summary>
    public const string ProviderSlow =
        "Penyedia AI sedang tidak merespons tepat waktu, jadi jawaban belum bisa dibuat. Silakan kirim ulang pesan Anda sebentar lagi.";
}
