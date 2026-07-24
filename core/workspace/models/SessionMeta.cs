using System.Collections.Generic;

namespace MMslcOverlay.Core.Workspace.Models;

public class SessionMeta
{
    public string SessionId { get; set; } = string.Empty;
    
    public long CreatedAt { get; set; }
    
    public long LastUpdatedAt { get; set; }
    
    // Danh sách các chunk đã sealed (ví dụ: "seg_001", "seg_002")
    public List<string> SealedChunks { get; set; } = new();
    
    // Trạng thái hiện tại của chunk active (nếu cần track thêm)
    public string ActiveChunkId { get; set; } = "active";
}
