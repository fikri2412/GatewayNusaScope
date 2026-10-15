using OperatorAi.Shared.Options;
using OperatorAi.Shared.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OperatorAi.Worker.Specialist.Documents;

/// <summary>Isi teks satu halaman PDF; <paramref name="HasImages"/> menandai halaman dengan gambar/grafik tertanam.</summary>
public sealed record PdfPageContent(int PageNumber, string Text, bool HasImages);

/// <summary>
/// Ekstraksi teks PDF lewat PdfPig (murni managed, tanpa native) dengan batas halaman dan karakter dari
/// <see cref="DocumentRuntimeLimits"/> snapshot job yang gagal eksplisit — tidak ada pemotongan diam-diam.
/// Nomor halaman berasal dari PDF itu sendiri, bukan urutan iterasi.
/// </summary>
public static class PdfTextExtractor
{
    public static IReadOnlyList<PdfPageContent> Extract(string path, DocumentRuntimeLimits limits, CancellationToken ct)
    {
        using var document = Open(path);
        if (document.NumberOfPages > limits.MaxPages)
            throw new DocumentInputException(
                $"PDF memuat {document.NumberOfPages} halaman; maksimal {limits.MaxPages} halaman per analisis.");

        var pages = new List<PdfPageContent>(document.NumberOfPages);
        var characters = 0;
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page, true).Trim();
            characters += text.Length;
            if (characters > limits.MaxExtractedChars)
                throw new DocumentInputException(
                    $"Teks PDF melebihi batas {limits.MaxExtractedChars} karakter (terlampaui pada halaman {page.Number}).");
            pages.Add(new PdfPageContent(page.Number, text, page.NumberOfImages > 0));
        }

        if (pages.Count == 0) throw new DocumentInputException("PDF tidak memuat halaman.");
        return pages;
    }

    private static PdfDocument Open(string path)
    {
        try
        {
            return PdfDocument.Open(path);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new DocumentInputException("PDF terkunci password sehingga isinya tidak dapat dibaca.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // PdfPig melempar PdfDocumentFormatException/exception parser lain untuk file rusak; semuanya input dokumen.
            throw new DocumentInputException("PDF tidak dapat dibaca (file rusak atau bukan PDF yang valid).", ex);
        }
    }
}
