using System;

namespace MMslcOverlay.Core.Workspace.Models;

public class Annotation
{
    public long Id { get; set; }
    
    // "GLOBAL" | "SEGMENT"
    public string Scope { get; set; } = "GLOBAL";
    
    // "{chunk_id}:{segment_id}" nếu scope=SEGMENT
    public string? SegmentRef { get; set; }
    
    // "NOTE" | "BOOKMARK" | "HIGHLIGHT"
    public string Type { get; set; } = "NOTE";
    
    public string? Content { get; set; }
    
    public string? Color { get; set; }
    
    // Unix timestamp ms
    public long CreatedAt { get; set; }
}
