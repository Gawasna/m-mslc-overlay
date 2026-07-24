using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace MMslcOverlay.Views.Controls;

/// <summary>
/// Vẽ khoảng gap/đứt gãy giữa các Chunk (Page Break).
/// </summary>
public class PageBreakRenderer : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public IEnumerable<int> PageBreakOffsets { get; set; } = new List<int>();

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document == null || !System.Linq.Enumerable.Any(PageBreakOffsets)) return;

        var pen = new Pen(new SolidColorBrush(Color.Parse("#E0E0E0")), 1)
        {
            DashStyle = DashStyle.Dash
        };

        foreach (var offset in PageBreakOffsets)
        {
            if (offset > textView.Document.TextLength) continue;

            var line = textView.Document.GetLineByOffset(offset);
            if (line == null) continue;

            var visualLine = textView.GetVisualLine(line.LineNumber);
            if (visualLine == null) continue;

            double y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
            drawingContext.DrawLine(pen, new Point(0, y - 2), new Point(textView.Bounds.Width, y - 2));
        }
    }
}
