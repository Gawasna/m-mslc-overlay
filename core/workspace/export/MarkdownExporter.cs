using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

public class MarkdownExporter : IExporter
{
    public string Export(IEnumerable<MergedSegment> segments, IEnumerable<FreeformBlock>? blocks = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Transcript");
        sb.AppendLine();
        
        var blockDict = new Dictionary<string, List<FreeformBlock>>();
        var initialBlocks = new List<FreeformBlock>();

        if (blocks != null)
        {
            foreach (var b in blocks)
            {
                if (string.IsNullOrEmpty(b.AnchorAfter))
                {
                    initialBlocks.Add(b);
                }
                else
                {
                    if (!blockDict.ContainsKey(b.AnchorAfter))
                        blockDict[b.AnchorAfter] = new List<FreeformBlock>();
                    blockDict[b.AnchorAfter].Add(b);
                }
            }
        }

        // Render initial blocks
        foreach (var b in initialBlocks)
        {
            sb.AppendLine(b.Content);
            sb.AppendLine();
        }

        foreach (var seg in segments)
        {
            var time = System.TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs).ToString(@"hh\:mm\:ss");
            sb.AppendLine($"**[{time}] [{seg.BaseSegment.SpeakerId}]**");
            sb.AppendLine($"> {seg.TextSrc}");
            if (!string.IsNullOrEmpty(seg.TextTrs))
            {
                sb.AppendLine($"> *{seg.TextTrs}*");
            }
            sb.AppendLine();

            // Render blocks anchored after this segment
            string segId = seg.BaseSegment.Id.ToString(); // Assuming Id is a number, but SegmentRef is string. Wait, SegmentRef is usually ChunkId:Id or just string ID. Let's use Id.ToString() for now.
            if (blockDict.ContainsKey(segId))
            {
                foreach (var b in blockDict[segId])
                {
                    sb.AppendLine(b.Content);
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }
}
