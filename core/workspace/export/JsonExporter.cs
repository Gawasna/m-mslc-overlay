using System;
using System.Collections.Generic;
using System.Text.Json;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Export;

public class JsonExporter : IExporter
{
    public string ContentMode { get; set; } = "Song ngữ (EN + VI)";

    public string Export(IEnumerable<MergedSegment> segments, IEnumerable<FreeformBlock>? blocks = null)
    {
        var list = new List<object>();

        foreach (var seg in segments)
        {
            long startMs = seg.BaseSegment.GetMediaStartMs();
            long endMs = seg.BaseSegment.GetMediaEndMs();
            TimeSpan start = TimeSpan.FromMilliseconds(startMs);
            TimeSpan end = TimeSpan.FromMilliseconds(endMs);

            list.Add(new
            {
                startMs,
                endMs,
                startTime = $"{(int)start.TotalHours:00}:{start.Minutes:00}:{start.Seconds:00}.{start.Milliseconds:03}",
                endTime = $"{(int)end.TotalHours:00}:{end.Minutes:00}:{end.Seconds:00}.{end.Milliseconds:03}",
                speaker = seg.BaseSegment.SpeakerId,
                textSrc = seg.TextSrc,
                textTrs = seg.TextTrs
            });
        }

        if (blocks != null)
        {
            foreach (var b in blocks)
            {
                list.Add(new
                {
                    type = "note",
                    id = b.Id,
                    content = b.Content
                });
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(list, options);
    }
}
