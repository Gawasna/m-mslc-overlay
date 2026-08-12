using System;
using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

/// <summary>
/// Exports transcript to Advanced SubStation Alpha (.ass) format.
/// Produces v4+ compliant output with optional style section.
/// </summary>
public class AssExporter : IExporter
{
    public string ContentMode { get; set; } = "Song ngữ (EN + VI)";

    /// <summary>
    /// When false, only the [Events] section is written (style-stripped output).
    /// </summary>
    public bool IncludeStyles { get; set; } = true;

    public string Export(IEnumerable<MergedSegment> segments, IEnumerable<FreeformBlock>? blocks = null)
    {
        var sb = new StringBuilder();

        // ── Script Info ──────────────────────────────────────────────
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("Collisions: Normal");
        sb.AppendLine("PlayResX: 1920");
        sb.AppendLine("PlayResY: 1080");
        sb.AppendLine("Timer: 100.0000");
        sb.AppendLine($"Title: Exported by MSLC Overlay — {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine();

        // ── V4+ Styles ────────────────────────────────────────────────
        if (IncludeStyles)
        {
            sb.AppendLine("[V4+ Styles]");
            sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
            // White text with black outline (cinema standard)
            sb.AppendLine("Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,10,10,30,1");
            // Translated line: slightly smaller, light-blue tint
            sb.AppendLine("Style: Translation,Arial,40,&H00FFE4B5,&H000000FF,&H00000000,&H80000000,0,1,0,0,100,100,0,0,1,2,1,2,10,10,30,1");
            sb.AppendLine();
        }

        // ── Events ────────────────────────────────────────────────────
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        bool notesOnly = ContentMode.Contains("Notes");
        bool enOnly   = ContentMode == "Chỉ Tiếng Anh (EN)";
        bool viOnly   = ContentMode == "Chỉ Tiếng Việt (VI)";

        if (!notesOnly)
        {
            foreach (var seg in segments)
            {
                var start = TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs);
                var end = TimeSpan.FromMilliseconds(seg.BaseSegment.TsEndMs);
                string startStr = FormatAssTime(start);
                string endStr = FormatAssTime(end);

                if (enOnly)
                {
                    string text = EscapeAssText(seg.TextSrc);
                    sb.AppendLine($"Dialogue: 0,{startStr},{endStr},Default,,0,0,0,,{text}");
                }
                else if (viOnly)
                {
                    string content = !string.IsNullOrEmpty(seg.TextTrs) ? seg.TextTrs : seg.TextSrc;
                    sb.AppendLine($"Dialogue: 0,{startStr},{endStr},Default,,0,0,0,,{EscapeAssText(content)}");
                }
                else
                {
                    // Bilingual: original on Default style, translation on Translation style
                    sb.AppendLine($"Dialogue: 0,{startStr},{endStr},Default,,0,0,0,,{EscapeAssText(seg.TextSrc)}");
                    if (!string.IsNullOrEmpty(seg.TextTrs))
                    {
                        sb.AppendLine($"Dialogue: 0,{startStr},{endStr},Translation,,0,0,0,,{EscapeAssText(seg.TextTrs)}");
                    }
                }
            }
        }

        // Notes blocks rendered as comment-style events at t=0
        if ((ContentMode.Contains("Cả 2") || notesOnly) && blocks != null)
        {
            foreach (var b in blocks)
            {
                string text = EscapeAssText($"[NOTE] {b.Content}");
                sb.AppendLine($"Dialogue: 0,0:00:00.00,0:00:00.00,Default,,0,0,0,,{text}");
            }
        }

        return sb.ToString();
    }

    // ASS time format: H:MM:SS.CC (centiseconds)
    private static string FormatAssTime(TimeSpan ts)
    {
        int centiseconds = ts.Milliseconds / 10;
        return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{centiseconds:00}";
    }

    // ASS escaping: { } used for override tags, \N for newline
    private static string EscapeAssText(string text)
    {
        return text.Replace("{", "\\{").Replace("}", "\\}").Replace("\n", "\\N").Replace("\r", "");
    }
}
