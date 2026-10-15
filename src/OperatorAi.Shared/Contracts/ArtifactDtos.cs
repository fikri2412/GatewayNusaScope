namespace OperatorAi.Shared.Contracts;

/// <summary>Artifact tanpa isi: dasar daftar tab Canvas dan kartu referensi di chat.</summary>
/// <param name="SourceMessageId">Pesan user pemicu bila artifact dibuat AI; cocok dengan <c>MessageDto.SourceMessageId</c> jawabannya.</param>
public sealed record ArtifactSummaryDto(Guid Id, string Kind, string Title, string? Language, int CurrentVersion,
    long? SourceMessageId, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Artifact dengan isi versi aktif.</summary>
public sealed record ArtifactDto(Guid Id, string Kind, string Title, string? Language, int CurrentVersion,
    long? SourceMessageId, DateTime CreatedAt, DateTime UpdatedAt, string Content);

/// <summary>Satu entri riwayat versi; <c>Length</c> = jumlah karakter isinya, supaya daftar tidak membawa semua isi.</summary>
public sealed record ArtifactVersionDto(int Version, string Source, DateTime CreatedAt, int Length);

/// <summary>Isi satu versi lama (untuk melihat atau membandingkan sebelum dipulihkan).</summary>
public sealed record ArtifactVersionContentDto(int Version, string Source, DateTime CreatedAt, string Content);
