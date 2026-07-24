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

    private TextAnchor? _magicCursorAnchor;

    public int MagicCursorOffset 
    { 
        get => _magicCursorAnchor?.Offset ?? 0; 
        set 
        {
            if (_magicCursorAnchor != null)
                _magicCursorAnchor.SurviveDeletion = false; 
            _magicCursorAnchor = Document.CreateAnchor(value);
            _magicCursorAnchor.MovementType = AnchorMovementType.AfterInsertion;
        }
    }

    public MagicCursorViewModel MagicCursor { get; }
    public ScrollModeController ScrollController { get; } = new ScrollModeController();

    public PaperSheetViewModel(WorkspaceService workspace)
    {
        _workspace = workspace;
        MagicCursor = new MagicCursorViewModel(() => MagicCursorOffset);

        if (_workspace.IngestionService != null)
        {
            _workspace.IngestionService.SegmentAdded += OnSegmentAdded;
        }
        
        LoadInitialState();
    }

    public System.Collections.ObjectModel.ObservableCollection<int> PageBreakOffsets { get; } = new();

    private void LoadInitialState()
    {
        var allSegments = _workspace.SegmentRepo?.GetMergedSegments();
        if (allSegments == null) return;

        var sb = new StringBuilder();
        string? currentChunk = null;
        
        foreach (var seg in allSegments)
        {
            if (currentChunk != null && currentChunk != seg.BaseSegment.ChunkId)
            {
                PageBreakOffsets.Add(sb.Length);
            }
            currentChunk = seg.BaseSegment.ChunkId;
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
            int insertOffset = MagicCursorOffset;
            Document.Insert(insertOffset, textToInsert);
            // Anchor automatically moves because MovementType is AfterInsertion, but just to be sure we set it to end of inserted text if needed.
            // Since it's AfterInsertion, the anchor moves to the end of the inserted text automatically.
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
