using System;

namespace MMslcOverlay.Core.Workspace.Models;

public class MergedSegment
{
    // The underlying machine truth segment
    public Segment BaseSegment { get; set; }
    
    // Patched values (if any)
    public string TextSrc { get; set; }
    public string? TextTrs { get; set; }
    public string? SpeakerId { get; set; }
    
    public MergedSegment(Segment baseSegment)
    {
        BaseSegment = baseSegment;
        TextSrc = baseSegment.TextSrc;
        TextTrs = baseSegment.TextTrs;
        SpeakerId = baseSegment.SpeakerId;
    }
    
    // Thuộc tính tiện ích
    public string SegmentRef => $"{BaseSegment.ChunkId}:{BaseSegment.Id}";
}
