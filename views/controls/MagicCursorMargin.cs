using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace MMslcOverlay.Views.Controls;

/// <summary>
/// Hiển thị Indicator của Magic Cursor ở margin bên trái.
/// </summary>
public class MagicCursorMargin : AbstractMargin
{
    public int MagicCursorOffset { get; set; } = 0;

    public MagicCursorMargin()
    {
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (TextView == null || !TextView.VisualLinesValid) return;

        var brush = new SolidColorBrush(Color.Parse("#00E5FF")); // Cyberpunk cyan
        var pen = new Pen(brush, 2);

        var docLine = Document.GetLineByOffset(MagicCursorOffset);
        if (docLine != null)
        {
            var visualLine = TextView.GetVisualLine(docLine.LineNumber);
            if (visualLine != null)
            {
                double y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset;
                
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(0, y), true);
                    ctx.LineTo(new Point(10, y + 5));
                    ctx.LineTo(new Point(0, y + 10));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(brush, pen, geom);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(12, 0); 
    }
}
