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
        // For phase 3 mock, allow all insertions
        return true; 
    }

    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        yield return segment;
    }
}
