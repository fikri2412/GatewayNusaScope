using OperatorAi.Shared.Contracts;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OperatorAi.Worker.Specialist.Pdf;

public sealed class ReportDocument(string title, AnalysisResult analysis, DateTime createdAt) : IDocument
{
    public DocumentMetadata GetMetadata() => new() { Title = title, Author = "Operator AI" };

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10));
            page.Header().Column(header =>
            {
                header.Spacing(5);
                header.Item().Text(title).FontSize(18).Bold();
                header.Item().Text($"Dibuat: {createdAt:dd MMMM yyyy}").FontSize(9);
                header.Item().LineHorizontal(1);
            });
            page.Content().PaddingVertical(14).Column(body =>
            {
                body.Spacing(8);
                body.Item().Text("Ringkasan").FontSize(13).Bold();
                body.Item().Text(analysis.Ringkasan);
                body.Item().PaddingTop(8).Text("Temuan Masalah").FontSize(13).Bold();
                if (analysis.TemuanMasalah.Count == 0)
                    body.Item().Text("Tidak ada temuan masalah yang dilaporkan.");
                else
                    body.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
                        table.Header(header =>
                        {
                            header.Cell().Element(HeaderCell).Text("Masalah").Bold();
                            header.Cell().Element(HeaderCell).Text("Dampak").Bold();
                        });
                        foreach (var finding in analysis.TemuanMasalah)
                        {
                            table.Cell().Element(Cell).Text(finding.Masalah);
                            table.Cell().Element(Cell).Text(finding.Dampak);
                        }
                    });
                body.Item().PaddingTop(8).Text("Rekomendasi Digitalisasi").FontSize(13).Bold();
                if (analysis.RekomendasiDigitalisasi.Count == 0)
                    body.Item().Text("Tidak ada rekomendasi yang dilaporkan.");
                else
                    body.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(3);
                            columns.RelativeColumn();
                        });
                        table.Header(header =>
                        {
                            header.Cell().Element(HeaderCell).Text("Langkah").Bold();
                            header.Cell().Element(HeaderCell).Text("Manfaat").Bold();
                            header.Cell().Element(HeaderCell).Text("Prioritas").Bold();
                        });
                        foreach (var recommendation in analysis.RekomendasiDigitalisasi.OrderBy(r => r.Prioritas switch
                                 { "tinggi" => 0, "sedang" => 1, _ => 2 }))
                        {
                            table.Cell().Element(Cell).Text(recommendation.Langkah);
                            table.Cell().Element(Cell).Text(recommendation.Manfaat);
                            table.Cell().Element(Cell).Text(recommendation.Prioritas);
                        }
                    });
                body.Item().PaddingTop(8).Text("Rancangan Tabel").FontSize(13).Bold();
                if (analysis.RancanganTabel.Count == 0)
                    body.Item().Text("Tidak ada rancangan tabel yang dilaporkan.");
                foreach (var table in analysis.RancanganTabel)
                {
                    body.Item().Text(table.Nama).Bold();
                    foreach (var column in table.Kolom)
                        body.Item().PaddingLeft(10).Text($"• {column}");
                }
                if (!string.IsNullOrWhiteSpace(analysis.Catatan))
                {
                    body.Item().PaddingTop(8).Text("Catatan").FontSize(13).Bold();
                    body.Item().Text(analysis.Catatan);
                }
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("Halaman ");
                text.CurrentPageNumber();
                text.Span(" dari ");
                text.TotalPages();
            });
        });
    }

    private static IContainer Cell(IContainer container) => container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5);
    private static IContainer HeaderCell(IContainer container) => container.BorderBottom(1).Padding(5);
}
