using System;

namespace MMslcOverlay.Core.Workspace.Models;

public class UiState
{
    public string Key { get; set; } = string.Empty;
    
    public string Value { get; set; } = string.Empty;
    
    public long UpdatedAt { get; set; }
}
