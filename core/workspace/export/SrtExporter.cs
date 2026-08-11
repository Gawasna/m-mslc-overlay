using System;
using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

public class SrtExporter : IExporter
{
    public string ContentMode { get; set; } = "Song ngữ (EN + VI)";

    public string Export(IEnumerable<MergedSegment> segments, IEnumerable<FreeformBlock>? blocks = null)
    {
        var sb = new StringBuilder();
        int index = 1;

        bool notesOnly = ContentMode.Contains("Notes");
        bool enOnly = ContentMode.Contains("English") || ContentMode.Contains("EN");
        bool viOnly = ContentMode.Contains("Vietnamese") || ContentMode.Contains("VI");

        if (!notesOnly)
        {
            foreach (var seg in segments)
            {
                TimeSpan start = TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs);
                TimeSpan end = TimeSpan.FromMilliseconds(seg.BaseSegment.TsEndMs);

                sb.AppendLine(index.ToString());
                sb.AppendLine($"{FormatTime(start)} --> {FormatTime(end)}");

                string content;
                if (enOnly)
                {
                    content = seg.TextSrc;
                }
                else if (viOnly)
                {
                    content = !string.IsNullOrEmpty(seg.TextTrs) ? seg.TextTrs : seg.TextSrc;
                }
                else
                {
                    content = string.IsNullOrEmpty(seg.TextTrs) ? seg.TextSrc : $"{seg.TextSrc}\n{seg.TextTrs}";
                }

                sb.AppendLine(content);
                sb.AppendLine();
                index++;
            }
        }

        if ((ContentMode.Contains("Cả 2") || notesOnly) && blocks != null)
        {
            foreach (var b in blocks)
            {
                sb.AppendLine(index.ToString());
                sb.AppendLine("00:00:00,000 --> 00:00:00,000");
                sb.AppendLine($"[NOTE] {b.Content}");
                sb.AppendLine();
                index++;
            }
        }

        return sb.ToString();
    }

    private string FormatTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }
}
