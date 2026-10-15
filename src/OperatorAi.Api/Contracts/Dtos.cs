using System.Collections.Immutable;

namespace OperatorAi.Api.Contracts;

/// <summary>
/// Kebijakan lampiran efektif (<c>GET /api/attachment-policy</c>) dari satu snapshot <c>ExecutionPolicy</c>:
/// nilai yang sama dengan yang ditegakkan unggahan dan pengiriman, bukan default yang di-hardcode klien.
/// </summary>
/// <param name="MaxBytes">Batas ukuran satu lampiran dalam byte.</param>
/// <param name="AllowedExtensions">Ekstensi kanonik huruf kecil yang diterima unggahan.</param>
/// <param name="MaxAttachmentsPerMessage">Batas jumlah id lampiran mentah per pesan (sebelum deduplikasi).</param>
/// <param name="MaxImagePixels">Batas piksel gambar sebelum decode penuh, saat unggahan dan pemeriksaan worker.</param>
/// <param name="SourceSettingsVersion">Versi snapshot gateway yang berlaku; null = baseline lokal.</param>
/// <param name="PolicyHash">Hash snapshot kebijakan yang berlaku; UI bisa mendeteksi kebijakan berubah.</param>
public sealed record AttachmentPolicyDto(long MaxBytes, ImmutableArray<string> AllowedExtensions, int MaxAttachmentsPerMessage,
    int MaxImagePixels, string? SourceSettingsVersion, string PolicyHash);
