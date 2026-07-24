using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using System.Text.RegularExpressions;

namespace MMslcOverlay.Views.Controls;

/// <summary>
/// Ngăn chặn người dùng chỉnh sửa vào các vùng metada/cấu trúc của Machine Segment.
/// Spec: protect toàn bộ segment content, implement SegmentEditSession cho human edit.
/// </summary>
public partial class MachineSegmentProtector : IReadOnlySectionProvider
{
    private readonly TextDocument _document;

    [GeneratedRegex(@"^\[.*?\]\s\[.*?\]\s")]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"^\s*↳\s\[")]
    private static partial Regex TransRegex();

    public MachineSegmentProtector(TextDocument document)
    {
        _document = document;
    }

    public bool CanInsert(int offset)
    {
        if (_document == null || offset < 0 || offset > _document.TextLength) return true;

        var line = _document.GetLineByOffset(offset);
        string text = _document.GetText(line);

        // Protect the entire line if it contains machine segment metadata or translation
        if (MetaRegex().IsMatch(text) || TransRegex().IsMatch(text))
        {
            return false;
        }

        return true; 
    }

    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        if (_document == null)
        {
            yield return segment;
            yield break;
        }

        int startOffset = segment.Offset;
        int endOffset = segment.EndOffset;
        int currentOffset = startOffset;

        while (currentOffset < endOffset)
        {
            var line = _document.GetLineByOffset(currentOffset);
            string text = _document.GetText(line);
            
            bool isProtected = MetaRegex().IsMatch(text) || TransRegex().IsMatch(text);

            int endOfCurrentChunk = System.Math.Min(line.EndOffset, endOffset);
            
            if (!isProtected)
            {
                if (endOfCurrentChunk > currentOffset)
                {
                    yield return new SimpleSegment(currentOffset, endOfCurrentChunk - currentOffset);
                }
            }
            
            // If the chunk includes the newline, and we are not protected, we can yield the newline as well.
            if (!isProtected && endOffset >= line.NextLine?.Offset)
            {
                yield return new SimpleSegment(line.EndOffset, line.DelimiterLength);
            }

            currentOffset = line.NextLine?.Offset ?? endOffset;
        }
    }
}
