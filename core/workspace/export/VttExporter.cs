using System;
using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

/// <summary>
/// Exports transcript to WebVTT (.vtt) format.
/// Compliant with the W3C WebVTT specification.
/// </summary>
public class VttExporter : IExporter
{
    public string ContentMode { get; set; } = "Song ngữ (EN + VI)";

    public string Export(IEnumerable<MergedSegment> segments, IEnumerable<FreeformBlock>? blocks = null)
    {
        var sb = new StringBuilder();

        // WebVTT header — mandatory
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        bool notesOnly = ContentMode.Contains("Notes");
        bool enOnly = ContentMode.Contains("English") || (ContentMode.Contains("EN") && !ContentMode.Contains("EN + VI"));
        bool viOnly = ContentMode.Contains("Vietnamese") || ContentMode.Contains("VI");

        int cueIndex = 1;

        if (!notesOnly)
        {
            foreach (var seg in segments)
            {
                var start = TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs);
                var end = TimeSpan.FromMilliseconds(seg.BaseSegment.TsEndMs);

                string cueText;
                if (enOnly)
                {
                    cueText = seg.TextSrc;
                }
                else if (viOnly)
                {
                    cueText = !string.IsNullOrEmpty(seg.TextTrs) ? seg.TextTrs : seg.TextSrc;
                }
                else
                {
                    // Bilingual: two lines — VTT supports multi-line cues natively
                    cueText = string.IsNullOrEmpty(seg.TextTrs)
                        ? seg.TextSrc
                        : $"{seg.TextSrc}\n{seg.TextTrs}";
                }

                sb.AppendLine(cueIndex.ToString());
                sb.AppendLine($"{FormatVttTime(start)} --> {FormatVttTime(end)}");
                sb.AppendLine(cueText);
                sb.AppendLine();
                cueIndex++;
            }
        }

        // Notes blocks as NOTE comments (VTT spec supports NOTE blocks)
        if ((ContentMode.Contains("Cả 2") || notesOnly) && blocks != null)
        {
            foreach (var b in blocks)
            {
                sb.AppendLine("NOTE");
                sb.AppendLine(b.Content);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // WebVTT time format: HH:MM:SS.mmm
    private static string FormatVttTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }
}
