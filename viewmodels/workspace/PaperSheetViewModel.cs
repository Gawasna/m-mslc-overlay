using System;
using System.Text;
using AvaloniaEdit.Document;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Services.Workspace;

namespace MMslcOverlay.ViewModels.Workspace;

public class PaperSheetViewModel
{
    public TextDocument Document { get; } = new TextDocument();
    public UndoRedoStack History { get; } = new UndoRedoStack();

    private readonly WorkspaceService _workspace;

    public int MagicCursorOffset { get; set; } = 0;

    public PaperSheetViewModel(WorkspaceService workspace)
    {
        _workspace = workspace;

        if (_workspace.IngestionService != null)
        {
            _workspace.IngestionService.SegmentAdded += OnSegmentAdded;
        }
        
        LoadInitialState();
    }

    private void LoadInitialState()
    {
        var allSegments = _workspace.SegmentRepo?.GetMergedSegments();
        if (allSegments == null) return;

        var sb = new StringBuilder();
        foreach (var seg in allSegments)
        {
            sb.AppendLine(FormatSegmentForEditor(seg));
        }

        Document.Text = sb.ToString();
        MagicCursorOffset = Document.TextLength;
    }

    private void OnSegmentAdded(Segment segment)
    {
        string textToInsert = FormatSegmentForEditor(new MergedSegment(segment));
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Document.Insert(MagicCursorOffset, textToInsert);
            MagicCursorOffset += textToInsert.Length;
        });
    }

    private string FormatSegmentForEditor(MergedSegment seg)
    {
        TimeSpan ts = TimeSpan.FromMilliseconds(seg.BaseSegment.TsStartMs);
        string tsFormatted = $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        string spk = string.IsNullOrEmpty(seg.BaseSegment.SpeakerId) ? "UNK" : seg.BaseSegment.SpeakerId;

        string result = $"[{tsFormatted}] [{spk}] {seg.TextSrc}\n";
        if (!string.IsNullOrEmpty(seg.TextTrs))
        {
            result += $"  ↳ [{seg.TextTrs}]\n";
        }
        return result;
    }
}
