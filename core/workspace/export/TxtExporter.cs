using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

public class TxtExporter : IExporter
{
    public string ContentMode { get; set; } = "Song ngữ (EN + VI)";

    public string Export(IEnumerable<MergedSegment> segments, IEnumerable<FreeformBlock>? blocks = null)
    {
        var sb = new StringBuilder();

        bool notesOnly = ContentMode.Contains("Notes");
        bool enOnly = ContentMode.Contains("English") || ContentMode.Contains("EN");
        bool viOnly = ContentMode.Contains("Vietnamese") || ContentMode.Contains("VI");

        if (!notesOnly)
        {
            foreach (var seg in segments)
            {
                var time = System.TimeSpan.FromMilliseconds(seg.BaseSegment.GetMediaStartMs()).ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] [{seg.BaseSegment.SpeakerId}]");

                if (enOnly)
                {
                    sb.AppendLine(seg.TextSrc);
                }
                else if (viOnly)
                {
                    sb.AppendLine(!string.IsNullOrEmpty(seg.TextTrs) ? seg.TextTrs : seg.TextSrc);
                }
                else
                {
                    sb.AppendLine(seg.TextSrc);
                    if (!string.IsNullOrEmpty(seg.TextTrs))
                    {
                        sb.AppendLine($"  ↳ {seg.TextTrs}");
                    }
                }
                sb.AppendLine();
            }
        }

        if ((ContentMode.Contains("Cả 2") || notesOnly) && blocks != null)
        {
            sb.AppendLine("--- NOTES ---");
            foreach (var b in blocks)
            {
                sb.AppendLine($"• {b.Content}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
