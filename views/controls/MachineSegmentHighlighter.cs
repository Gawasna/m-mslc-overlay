using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace MMslcOverlay.Views.Controls;

/// <summary>
/// Highlight segment khi hover (hiệu ứng nền mờ 3%).
/// </summary>
public class MachineSegmentHighlighter : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public int HoveredOffset { get; set; } = -1;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (HoveredOffset < 0 || textView.Document == null) return;

        var line = textView.Document.GetLineByOffset(HoveredOffset);
        if (line == null) return;

        var visualLine = textView.GetVisualLine(line.LineNumber);
        if (visualLine == null) return;

        var rects = BackgroundGeometryBuilder.GetRectsForSegment(textView, line);
        var brush = new SolidColorBrush(Color.Parse("#08000000")); // 3% opacity black
        
        foreach (var rect in rects)
        {
            drawingContext.DrawRectangle(brush, null, new Rect(0, rect.Y, textView.Bounds.Width, rect.Height));
        }
    }
}
