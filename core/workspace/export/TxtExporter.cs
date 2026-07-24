using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

public class TxtExporter : IExporter
{
    public string Export(IEnumerable<MergedSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            var time = System.TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs).ToString(@"hh\:mm\:ss");
            sb.AppendLine($"[{time}] [{seg.BaseSegment.SpeakerId}]");
            sb.AppendLine(seg.TextSrc);
            if (!string.IsNullOrEmpty(seg.TextTrs))
            {
                sb.AppendLine($"  ↳ {seg.TextTrs}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
