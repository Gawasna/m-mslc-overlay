using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MMslcOverlay.Core.Workspace.Models;

public class BridgeMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    // LOAD_DOCUMENT
    [JsonPropertyName("segments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BridgeSegment>? Segments { get; set; }

    [JsonPropertyName("freeformBlocks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BridgeFreeformBlock>? FreeformBlocks { get; set; }

    // INSERT_MACHINE_SEGMENT & APPLY_PATCH
    [JsonPropertyName("segId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SegId { get; set; }

    [JsonPropertyName("tsStartMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TsStartMs { get; set; }

    [JsonPropertyName("tsEndMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TsEndMs { get; set; }

    [JsonPropertyName("speakerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeakerId { get; set; }

    [JsonPropertyName("textSrc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextSrc { get; set; }

    [JsonPropertyName("textTrs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextTrs { get; set; }

    [JsonPropertyName("field")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; set; }

    [JsonPropertyName("newValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewValue { get; set; }

    // SET_MAGIC_CURSOR
    [JsonPropertyName("pos")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pos { get; set; }

    // SET_SCROLL_MODE
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    // FREEFORM_CHANGED
    [JsonPropertyName("blockId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockId { get; set; }

    [JsonPropertyName("anchorAfter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnchorAfter { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }
}

public class BridgeSegment
{
    [JsonPropertyName("segId")]
    public string SegId { get; set; } = string.Empty;
    
    [JsonPropertyName("tsStartMs")]
    public long TsStartMs { get; set; }
    
    [JsonPropertyName("tsEndMs")]
    public long TsEndMs { get; set; }
    
    [JsonPropertyName("speakerId")]
    public string SpeakerId { get; set; } = "UNK";
    
    [JsonPropertyName("textSrc")]
    public string TextSrc { get; set; } = string.Empty;
    
    [JsonPropertyName("textTrs")]
    public string? TextTrs { get; set; }
}

public class BridgeFreeformBlock
{
    [JsonPropertyName("blockId")]
    public string BlockId { get; set; } = string.Empty;
    
    [JsonPropertyName("anchorAfter")]
    public string? AnchorAfter { get; set; }
    
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
