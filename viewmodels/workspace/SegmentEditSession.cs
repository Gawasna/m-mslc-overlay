using System;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;

namespace MMslcOverlay.ViewModels.Workspace;

/// <summary>
/// Quản lý phiên chỉnh sửa ở cấp độ field-level cho một Machine Segment.
/// Phục vụ cho tính năng double-click để sửa text mà không phá vỡ mô hình Two-Source-of-Truth.
/// </summary>
public class SegmentEditSession
{
    private readonly UserDataRepository _userDataRepo;
    public MergedSegment Segment { get; }

    public SegmentEditSession(MergedSegment segment, UserDataRepository userDataRepo)
    {
        Segment = segment;
        _userDataRepo = userDataRepo;
    }

    /// <summary>
    /// Tính toán sự thay đổi và lưu thành PatchEvent.
    /// </summary>
    public void CommitEdit(string newTextSrc, string? newTextTrs)
    {
        if (Segment.TextSrc != newTextSrc)
        {
            var patch = new PatchEvent
            {
                EventType = "EDIT",
                SegmentRef = Segment.BaseSegment.Id.ToString(),
                Field = "TextSrc",
                ValueOld = Segment.TextSrc,
                ValueNew = newTextSrc,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            _userDataRepo.InsertPatchEvent(patch);
        }

        if (Segment.TextTrs != newTextTrs && newTextTrs != null)
        {
            var patchTrs = new PatchEvent
            {
                EventType = "EDIT",
                SegmentRef = Segment.BaseSegment.Id.ToString(),
                Field = "TextTrs",
                ValueOld = Segment.TextTrs,
                ValueNew = newTextTrs,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            _userDataRepo.InsertPatchEvent(patchTrs);
        }
    }
}
