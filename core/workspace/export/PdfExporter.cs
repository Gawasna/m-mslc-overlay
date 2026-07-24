using System.Collections.Generic;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

public class PdfExporter : IExporter
{
    public string Export(IEnumerable<MergedSegment> segments)
    {
        // For Phase 5, we mock the PDF layout output.
        // In real implementation, this would use QuestPDF or iTextSharp and return a file path or Base64 string.
        return "[PDF_BINARY_MOCK_CONTENT]";
    }
}
