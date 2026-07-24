using System;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Services;

/// <summary>
/// Trích xuất sự thay đổi (Patch) từ việc chỉnh sửa văn bản của người dùng.
/// </summary>
public class PatchExtractor
{
    public static PatchEvent? ComputeDelta(MergedSegment original, string newText, string field)
    {
        string? oldText = field == "TextSrc" ? original.TextSrc : original.TextTrs;
        
        if (oldText == newText) return null;

        return new PatchEvent
        {
            EventType = "EDIT",
            SegmentRef = original.BaseSegment.Id.ToString(),
            Field = field,
            ValueOld = oldText,
            ValueNew = newText,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}
