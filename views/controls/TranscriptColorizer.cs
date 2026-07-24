using Avalonia.Media;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Document;
using System.Text.RegularExpressions;

namespace MMslcOverlay.Views.Controls;

public partial class TranscriptColorizer : DocumentColorizingTransformer
{
    [GeneratedRegex(@"^\[.*?\]\s\[.*?\]\s")]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"^\s*↳\s\[(.*?)\]")]
    private static partial Regex TransRegex();

    protected override void ColorizeLine(DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        
        var match = MetaRegex().Match(text);
        if (match.Success)
        {
            ChangeLinePart(line.Offset, line.Offset + match.Length, element =>
            {
                element.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
                element.TextRunProperties.SetFontRenderingEmSize(0.1); 
            });
        }

        var transMatch = TransRegex().Match(text);
        if (transMatch.Success)
        {
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.Parse("#006699")));
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyle.Italic, FontWeight.Normal));
            });
        }
    }
}
