using OperatorAi.Shared.Queue;

namespace OperatorAi.Shared.Data;

public class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    /// <summary>Email opsional, disimpan huruf kecil; null = akun tanpa email (tidak bisa atur ulang password).</summary>
    public string? Email { get; set; }
    /// <summary>Diganti saat reset password; token akses lama ditolak bila stamp tidak cocok.</summary>
    public Guid SecurityStamp { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Conversation
{
    public Guid Id { get; set; }
    /// <summary><c>users.id</c> dalam bentuk teks (tanpa FK, sesuai DDL DESIGN bagian 5).</summary>
    public required string UserId { get; set; }
    public string? Title { get; set; }
    public string? ModelAlias { get; set; }
    /// <summary>Total token (prompt + completion) semua panggilan AI percakapan ini; dibandingkan dengan <c>Operator:MaxTokensPerConversation</c>.</summary>
    public long TokensUsed { get; set; }
    /// <summary>Token terukur hanya lower bound bila setidaknya satu panggilan tidak memberi usage.</summary>
    public bool UsageIncomplete { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Message
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string Role { get; set; }
    public string? Content { get; set; }
    public string? AiContentJson { get; set; }
    public bool IsVisible { get; set; } = true;
    public string? AttachmentIdsJson { get; set; }
    /// <summary>Tiket queue yang menghasilkan baris ini (assistant, hasil tool, catatan sistem); null untuk pesan user.</summary>
    public long? QueueItemId { get; set; }
    public string? FinishReason { get; set; }
    public string? ModelAlias { get; set; }
    /// <summary>Identitas panggilan streaming yang di-commit sebagai pesan ini; draft browser hanya sementara.</summary>
    public Guid? StreamId { get; set; }
    /// <summary>0 hanya untuk jawaban alternatif (regenerate/bandingkan); disalin dari tiket yang menulis baris ini.</summary>
    public bool IsPrimary { get; set; } = true;
    /// <summary>Total token (prompt + completion) tiket ini; null bila salah satu panggilan tanpa usage.</summary>
    public int? TokensUsed { get; set; }
    /// <summary>Waktu tunggu di queue sebelum tiket diklaim; null bila tidak diketahui (baris lama).</summary>
    public int? QueueWaitMs { get; set; }
    /// <summary>Waktu sampai delta konten pertama tiket ini; null untuk non-streaming.</summary>
    public int? FirstTokenMs { get; set; }
    /// <summary>Durasi tiket dari pembuatan baris queue sampai balasan akhir.</summary>
    public int? TotalMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Attachment
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string FileName { get; set; }
    public required string StoragePath { get; set; }
    public required string Kind { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class QueueItem
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string Type { get; set; }
    public required string PayloadJson { get; set; }
    public string? ExecutionPolicyJson { get; set; }
    public long? SourceMessageId { get; set; }
    /// <summary>ID idempotensi kiriman klien (§4.3); null untuk pengiriman biasa.</summary>
    public string? ClientRequestId { get; set; }
    /// <summary>0 hanya untuk tiket regenerate/bandingkan; callback menyalin nilai dari baris job.</summary>
    public bool IsPrimary { get; set; } = true;
    public string Status { get; set; } = Statuses.Pending;
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class Job
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string JobType { get; set; }
    public required string InputJson { get; set; }
    public string? ExecutionPolicyJson { get; set; }
    public long? SourceMessageId { get; set; }
    /// <summary>Disalin dari tiket pembuat job (§5).</summary>
    public bool IsPrimary { get; set; } = true;
    public string? OutputJson { get; set; }
    public string Status { get; set; } = Statuses.Pending;
    public long? DependsOnJobId { get; set; }
    public bool NotifyOperator { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>SHA-256 heksadesimal dari token opaque di cookie; token asli tidak pernah disimpan.</summary>
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>SHA-256 heksadesimal dari token pada tautan email.</summary>
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class SavedAnswer
{
    public Guid Id { get; set; }
    /// <summary><c>users.id</c> dalam bentuk teks (tanpa FK, sama seperti <c>conversations.user_id</c>).</summary>
    public required string UserId { get; set; }
    /// <summary>Snapshot pesan sumber; tanpa FK karena pesan bisa ikut terhapus bersama sesi.</summary>
    public long? MessageId { get; set; }
    /// <summary>Snapshot sesi sumber; tanpa FK (dikosongkan saat sesi dihapus, §4.4).</summary>
    public Guid? ConversationId { get; set; }
    public string? SessionTitle { get; set; }
    public string? ModelAlias { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Jenis artifact yang didukung Canvas. Nilai disimpan apa adanya di kolom <c>kind</c>.</summary>
public static class ArtifactKinds
{
    public const string Document = "dokumen", Code = "kode", Diagram = "diagram", Spreadsheet = "spreadsheet";
    public static readonly string[] All = [Document, Code, Diagram, Spreadsheet];
}

/// <summary>Asal sebuah versi artifact: user menyimpan, AI membuat/memperbarui, atau salinan versi lama (urungkan).</summary>
public static class ArtifactSources
{
    public const string User = "user", Ai = "ai", Restore = "restore";
}

/// <summary>
/// Konten kerja Canvas milik satu percakapan (dokumen Markdown atau kode). Isinya ada di <see cref="ArtifactVersion"/>;
/// baris ini hanya metadata + penunjuk versi aktif.
/// </summary>
public class Artifact
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string Kind { get; set; }
    public required string Title { get; set; }
    /// <summary>Bahasa kode (mis. <c>csharp</c>); null untuk dokumen dan diagram (isi diagram = sumber Mermaid).</summary>
    public string? Language { get; set; }
    /// <summary>Nomor versi aktif (mulai 1). Penyimpanan wajib menyebut versi dasarnya; beda berarti konflik.</summary>
    public int CurrentVersion { get; set; }
    /// <summary>Pesan user yang memicu AI membuat artifact ini; null untuk artifact buatan user.</summary>
    public long? SourceMessageId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Satu snapshot isi artifact. Tidak pernah diubah setelah ditulis; urungkan membuat versi baru.</summary>
public class ArtifactVersion
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }
    public int Version { get; set; }
    public required string Content { get; set; }
    public required string Source { get; set; }
    public DateTime CreatedAt { get; set; }
}
