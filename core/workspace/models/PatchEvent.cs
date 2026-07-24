using System;

namespace MMslcOverlay.Core.Workspace.Models;

public class PatchEvent
{
    public long Id { get; set; }
    
    // "PATCH" | "UNDO" | "REDO"
    public string EventType { get; set; } = "PATCH";
    
    // "{chunk_id}:{segment_id}" (vd: "seg_001:42")
    public string SegmentRef { get; set; } = string.Empty;
    
    // "text_src" | "text_trs" | "speaker_id"
    public string Field { get; set; } = string.Empty;
    
    // Giá trị trước khi thay đổi (dùng cho Undo)
    public string? ValueOld { get; set; }
    
    // Giá trị mới
    public string ValueNew { get; set; } = string.Empty;
    
    // Nếu event_type=UNDO: id của event bị đảo ngược
    public long? ReversesId { get; set; }
    
    // Unix timestamp ms
    public long CreatedAt { get; set; }
}
