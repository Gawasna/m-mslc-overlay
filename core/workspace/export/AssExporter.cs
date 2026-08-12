using System;
using System.Collections.Generic;
using System.Text;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

/// <summary>Advanced SubStation Alpha (.ass) export. PrimaryColour is &amp;HAABBGGRR.</summary>
public class AssExporter : IExporter
{
    public string ContentMode { get; set; } = "Song ngữ (EN + VI)";
    public bool IncludeStyles { get; set; } = true;
    public long TimeOffsetMs { get; set; }
    public string ColorPreset { get; set; } = "White";

    public string Export(IEnumerable<MergedSegment> segments, IEnumerable<FreeformBlock>? blocks = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("Collisions: Normal");
        sb.AppendLine("PlayResX: 1920");
        sb.AppendLine("PlayResY: 1080");
        sb.AppendLine("Timer: 100.0000");
        sb.AppendLine($"Title: Exported by MSLC Overlay — {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine();

        if (IncludeStyles)
        {
            string primary = ResolveAssPrimaryColour(ColorPreset);
            string translation = ResolveAssTranslationColour(ColorPreset);
            const string outline = "&H00000000";
            const string back = "&H80000000";
            const string secondary = "&H000000FF";

            sb.AppendLine("[V4+ Styles]");
            sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
            sb.AppendLine($"Style: Default,Arial,48,{primary},{secondary},{outline},{back},0,0,0,0,100,100,0,0,1,2,1,2,10,10,30,1");
            sb.AppendLine($"Style: Translation,Arial,40,{translation},{secondary},{outline},{back},0,1,0,0,100,100,0,0,1,2,1,2,10,10,30,1");
            sb.AppendLine();
        }

        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        bool notesOnly = ContentMode.Contains("Notes");
        bool enOnly = ContentMode.Contains("English") || (ContentMode.Contains("EN") && !ContentMode.Contains("EN + VI"));
        bool viOnly = ContentMode.Contains("Vietnamese") || ContentMode.Contains("VI");

        if (!notesOnly)
        {
            foreach (var seg in segments)
            {
                long startMs = Math.Max(0, seg.BaseSegment.GetMediaStartMs() + TimeOffsetMs);
                long endMs = Math.Max(0, seg.BaseSegment.GetMediaEndMs() + TimeOffsetMs);
                if (endMs < startMs) endMs = startMs;
                var start = TimeSpan.FromMilliseconds(startMs);
                var end = TimeSpan.FromMilliseconds(endMs);
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
                    sb.AppendLine($"Dialogue: 0,{startStr},{endStr},Default,,0,0,0,,{EscapeAssText(seg.TextSrc)}");
                    if (!string.IsNullOrEmpty(seg.TextTrs))
                    {
                        sb.AppendLine($"Dialogue: 0,{startStr},{endStr},Translation,,0,0,0,,{EscapeAssText(seg.TextTrs)}");
                    }
                }
            }
        }

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

    private static string FormatAssTime(TimeSpan ts)
    {
        int centiseconds = ts.Milliseconds / 10;
        return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{centiseconds:00}";
    }

    private static string EscapeAssText(string text)
    {
        return text.Replace("{", "\\{").Replace("}", "\\}").Replace("\n", "\\N").Replace("\r", "");
    }

    /// <summary>UI preset → ASS &amp;HAABBGGRR primary colour.</summary>
    public static string ResolveAssPrimaryColour(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
            return "&H00FFFFFF";

        string p = preset.Trim();
        if (p.Contains("Vàng", StringComparison.OrdinalIgnoreCase) || p.Contains("Yellow", StringComparison.OrdinalIgnoreCase))
            return "&H0000FFFF";
        if (p.Contains("Xanh dương", StringComparison.OrdinalIgnoreCase) || p.Contains("Cyan", StringComparison.OrdinalIgnoreCase))
            return "&H00FFFF00";
        if (p.Contains("Xanh lá", StringComparison.OrdinalIgnoreCase) || p.Contains("Green", StringComparison.OrdinalIgnoreCase) || p.Contains("Lime", StringComparison.OrdinalIgnoreCase))
            return "&H0000FF00";
        if (p.Contains("Cam", StringComparison.OrdinalIgnoreCase) || p.Contains("Orange", StringComparison.OrdinalIgnoreCase))
            return "&H0000A5FF";
        return "&H00FFFFFF";
    }

    public static string ResolveAssTranslationColour(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
            return "&H00FFE4B5";

        string p = preset.Trim();
        if (p.Contains("Vàng", StringComparison.OrdinalIgnoreCase) || p.Contains("Yellow", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Xanh dương", StringComparison.OrdinalIgnoreCase) || p.Contains("Cyan", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Xanh lá", StringComparison.OrdinalIgnoreCase) || p.Contains("Green", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Lime", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Cam", StringComparison.OrdinalIgnoreCase) || p.Contains("Orange", StringComparison.OrdinalIgnoreCase))
            return "&H00FFFFFF";
        return "&H00FFE4B5";
    }
}
