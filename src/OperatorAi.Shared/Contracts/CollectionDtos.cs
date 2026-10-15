namespace OperatorAi.Shared.Contracts;

/// <summary>
/// Jawaban tersimpan milik satu user: snapshot isi + judul sesi saat disimpan, jadi tetap terbaca setelah sesinya
/// dihapus. <c>SourceAvailable</c> = referensi sumber masih ada (sesi belum dihapus).
/// </summary>
public sealed record SavedAnswerDto(Guid Id, string Content, string? ModelAlias, string? SessionTitle,
    Guid? ConversationId, long? MessageId, DateTime CreatedAt, bool SourceAvailable);
