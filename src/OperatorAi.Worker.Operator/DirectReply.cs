using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OperatorAi.Shared.Contracts;
using OperatorAi.Shared.Data;
using OperatorAi.Shared.Options;
using OperatorAi.Shared.Queue;

namespace OperatorAi.Worker.Operator;

/// <summary>
/// Balasan langsung tanpa AI untuk job yang sudah selesai dan bentuknya dikenali lengkap: laporan analisis
/// (<see cref="AnalysisResult"/>) dan lampiran PDF. Bentuk yang tidak dikenal, permintaan transformasi/sintesis,
/// detail yang bukan "mendalam", atau instruksi tambahan admin tetap lewat AI supaya tidak ada aturan yang dilewati.
/// </summary>
internal static class DirectReply
{
    /// <summary><c>ExecutionPolicy.Detail</c> yang menyajikan seluruh isi laporan apa adanya.</summary>
    private const string DetailMendalam = "mendalam";

    /// <param name="InputJson">Metadata <c>langsung</c> dari tool: user minta hasil apa adanya.</param>
    private static bool IsDirect(string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("langsung", out var flag) && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <returns>Balasan siap kirim (teks + lampiran) bila output job lengkap; null → jalur AI.</returns>
    public static async Task<(string Text, string? AttachmentIdsJson)?> TryAsync(Job job, ExecutionPolicy policy,
        AppDbContext db, CancellationToken ct)
    {
        if (job.Status != Statuses.Done || !job.NotifyOperator || !IsDirect(job.InputJson)) return null;
        // Instruksi tambahan admin (gaya, aturan bisnis) tidak boleh dilewati balasan langsung.
        if (!string.IsNullOrWhiteSpace(policy.AdditionalInstructions)) return null;
        return job.JobType switch
        {
            // Laporan panjang hanya apa adanya bila setting percakapan memang meminta detail mendalam.
            JobTypes.Analyze when policy.Detail == DetailMendalam => FormatAnalysis(job.OutputJson) is { } text ? (text, null) : null,
            JobTypes.DocumentAnalyze => FormatDocument(job.OutputJson) is { } document ? (document, null) : null,
            JobTypes.Pdf => await FormatPdfAsync(job, db, ct),
            _ => null,
        };
    }

    private static string? FormatDocument(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return null;
        try { return DocumentAnalysisResult.Parse(outputJson).ToText(); }
        catch (FormatException) { return null; }
    }

    private static async Task<(string Text, string? AttachmentIdsJson)?> FormatPdfAsync(Job job, AppDbContext db, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(job.OutputJson)) return null;
            using var doc = JsonDocument.Parse(job.OutputJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("attachmentId", out var rawId) || rawId.ValueKind != JsonValueKind.String
                || !Guid.TryParse(rawId.GetString(), out var id)
                || !root.TryGetProperty("fileName", out var rawName) || rawName.ValueKind != JsonValueKind.String
                || rawName.GetString() is not { Length: > 0 } fileName)
                return null;

            // Lampiran harus milik percakapan job ini; output asing tidak pernah dikirim ke user.
            if (!await db.Attachments.AsNoTracking()
                    .AnyAsync(a => a.Id == id && a.ConversationId == job.ConversationId && a.Kind == AttachmentKinds.Output, ct))
                return null;

            return ($"Laporan PDF \"{fileName}\" sudah selesai dan bisa diunduh dari lampiran pesan ini.",
                JsonSerializer.Serialize(new[] { id }));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validasi lewat parser skema bersama (output AI adalah input tidak tepercaya) lalu render seluruh isinya.
    /// Output yang tidak sesuai skema → null (jalur AI), jadi tidak pernah ada laporan setengah jadi.
    /// </summary>
    private static string? FormatAnalysis(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return null;
        AnalysisResult analysis;
        try
        {
            analysis = AnalysisResult.Parse(outputJson);
        }
        catch (FormatException)
        {
            return null;
        }

        var text = new StringBuilder(analysis.Judul).Append("\n\n").Append(analysis.Ringkasan);
        if (analysis.TemuanMasalah.Count > 0)
        {
            text.Append("\n\nMasalah yang ditemukan:");
            foreach (var temuan in analysis.TemuanMasalah)
                text.Append("\n- ").Append(temuan.Masalah).Append(" — ").Append(temuan.Dampak);
        }
        if (analysis.RekomendasiDigitalisasi.Count > 0)
        {
            text.Append("\n\nRekomendasi digitalisasi:");
            foreach (var rekomendasi in analysis.RekomendasiDigitalisasi)
                text.Append("\n- ").Append(rekomendasi.Langkah).Append(" — ").Append(rekomendasi.Manfaat)
                    .Append(" (prioritas ").Append(rekomendasi.Prioritas).Append(')');
        }
        if (analysis.RancanganTabel.Count > 0)
        {
            text.Append("\n\nRancangan tabel:");
            foreach (var tabel in analysis.RancanganTabel)
                text.Append("\n- ").Append(tabel.Nama).Append(": ").Append(string.Join(", ", tabel.Kolom));
        }
        if (analysis.Catatan.Trim() is { Length: > 0 } catatan) text.Append("\n\nCatatan: ").Append(catatan);
        return text.ToString();
    }
}
