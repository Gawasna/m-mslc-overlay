using System;

namespace MMslcOverlay.Core.Workspace.Models;

public class FreeformBlock
{
    public long Id { get; set; }
    
    // "{chunk_id}:{segment_id}" segment mà block này đứng SAU
    // null = đứng trước tất cả segments (đầu document)
    public string? AnchorAfter { get; set; }
    
    // Nội dung text (Markdown supported)
    public string Content { get; set; } = string.Empty;
    
    // Unix timestamp ms
    public long CreatedAt { get; set; }
    
    // Unix timestamp ms
    public long UpdatedAt { get; set; }
}
