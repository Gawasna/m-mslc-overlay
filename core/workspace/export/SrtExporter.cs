using System;
using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

public class SrtExporter : IExporter
{
    public string Export(IEnumerable<MergedSegment> segments)
    {
        var sb = new StringBuilder();
        int index = 1;

        foreach (var seg in segments)
        {
            TimeSpan start = TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs);
            TimeSpan end = TimeSpan.FromMilliseconds(seg.BaseSegment.TsEndMs);

            sb.AppendLine(index.ToString());
            sb.AppendLine($"{FormatTime(start)} --> {FormatTime(end)}");
            
            string content = seg.TextSrc;
            if (!string.IsNullOrEmpty(seg.TextTrs))
            {
                content += $"\n{seg.TextTrs}";
            }
            
            sb.AppendLine(content);
            sb.AppendLine();
            index++;
        }

        return sb.ToString();
    }

    private string FormatTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }
}
