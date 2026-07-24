using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using System.Text.RegularExpressions;

namespace MMslcOverlay.Views.Controls;

public class TranscriptGutterMargin : AbstractMargin
{
    private readonly Regex _metaRegex = new Regex(@"^\[(.*?)\]\s\[(.*?)\]");
    private readonly TextDocument _document;

    public TranscriptGutterMargin(TextDocument document)
    {
        _document = document;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (TextView == null || !TextView.VisualLinesValid) return;

        var typeFace = new Typeface("Consolas");
        var brush = new SolidColorBrush(Color.Parse("#888888"));

        foreach (var line in TextView.VisualLines)
        {
            string text = _document.GetText(line.FirstDocumentLine);
            
            var match = _metaRegex.Match(text);
            if (match.Success)
            {
                string ts = match.Groups[1].Value;
                string spk = match.Groups[2].Value;
                
                var tsText = new FormattedText(ts, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeFace, 10, brush);
                var spkText = new FormattedText(spk, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Consolas", FontStyle.Normal, FontWeight.Bold), 9, brush);

                double y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset;
                
                context.DrawText(tsText, new Point(0, y));
                context.DrawText(spkText, new Point(0, y + 12));
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(60, 0); // Reserve 60px for the gutter
    }
}
