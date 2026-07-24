using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace MMslcOverlay.Views.Controls;

/// <summary>
/// Ngăn chặn người dùng chỉnh sửa vào các vùng metada/cấu trúc của Machine Segment.
/// </summary>
public class MachineSegmentProtector : IReadOnlySectionProvider
{
    private readonly TextDocument _document;

    public MachineSegmentProtector(TextDocument document)
    {
        _document = document;
    }

    public bool CanInsert(int offset)
    {
        if (_document == null || offset < 0 || offset > _document.TextLength) return true;

        var line = _document.GetLineByOffset(offset);
        string text = _document.GetText(line);
        int offsetInLine = offset - line.Offset;

        // Protect primary metadata: "[00:00:00] [SPK]"
        var metaRegex = new System.Text.RegularExpressions.Regex(@"^\[.*?\]\s\[.*?\]\s");
        var metaMatch = metaRegex.Match(text);
        if (metaMatch.Success && offsetInLine < metaMatch.Length)
        {
            return false;
        }

        // Protect translation metadata: "  ↳ ["
        var transRegex = new System.Text.RegularExpressions.Regex(@"^\s*↳\s\[");
        var transMatch = transRegex.Match(text);
        if (transMatch.Success && offsetInLine < transMatch.Length)
        {
            return false;
        }
        // Protect the closing bracket of translation metadata if offset is at the end of the line
        if (transMatch.Success && text.EndsWith("]") && offsetInLine == text.Length - 1)
        {
             return false;
        }

        return true; 
    }

    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        yield return segment;
    }
}
