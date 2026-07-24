using System.Collections.Generic;
using System.IO;
using System.Linq;
using MMslcOverlay.Core.Workspace.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MMslcOverlay.Core.Workspace.Export;

public class PdfExporter : IExporter
{
    public PdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public string Export(IEnumerable<MergedSegment> segments)
    {
        var tempFile = Path.GetTempFileName() + ".pdf";
        
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(12));

                page.Header().Text("Transcript").SemiBold().FontSize(20).FontColor(Colors.Blue.Darken2);

                page.Content().PaddingVertical(1, Unit.Centimetre).Column(col =>
                {
                    foreach (var seg in segments)
                    {
                        var time = System.TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs).ToString(@"hh\:mm\:ss");
                        col.Item().Text($"[{time}] [{seg.BaseSegment.SpeakerId}]").Bold();
                        col.Item().Text(seg.TextSrc);
                        if (!string.IsNullOrEmpty(seg.TextTrs))
                        {
                            col.Item().Text($"  ↳ {seg.TextTrs}").Italic().FontColor(Colors.Grey.Darken2);
                        }
                        col.Item().PaddingBottom(10);
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        })
        .GeneratePdf(tempFile);

        return tempFile; // Return path to generated PDF
    }
}
