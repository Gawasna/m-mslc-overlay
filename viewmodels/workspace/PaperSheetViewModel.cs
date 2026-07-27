using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using AvaloniaEdit.Document;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Services.Workspace;

namespace MMslcOverlay.ViewModels.Workspace;

public class PaperSheetViewModel : INotifyPropertyChanged
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

    // ─── UI State Properties for Chrome ───────────────────────────────
    private int _wordCount;
    public int WordCount
    {
        get => _wordCount;
        set { if (_wordCount != value) { _wordCount = value; OnPropertyChanged(); } }
    }

    private int _zoomPercent = 100;
    public int ZoomPercent
    {
        get => _zoomPercent;
        set { if (_zoomPercent != value) { _zoomPercent = value; OnPropertyChanged(); } }
    }

    private string _documentLanguage = "Bilingual";
    public string DocumentLanguage
    {
        get => _documentLanguage;
        set { if (_documentLanguage != value) { _documentLanguage = value; OnPropertyChanged(); } }
    }

    private bool _autoScroll = true;
    public bool AutoScroll
    {
        get => _autoScroll;
        set { if (_autoScroll != value) { _autoScroll = value; OnPropertyChanged(); } }
    }

    private bool _focusMode = false;
    public bool FocusMode
    {
        get => _focusMode;
        set { if (_focusMode != value) { _focusMode = value; OnPropertyChanged(); } }
    }

    private int _pageNumber = 1;
    public int PageNumber
    {
        get => _pageNumber;
        set { if (_pageNumber != value) { _pageNumber = value; OnPropertyChanged(); } }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ─── UI Actions ────────────────────────────────────────────────────────
    public void ZoomIn()
    {
        if (ZoomPercent < 300) ZoomPercent += 10;
    }

    public void ZoomOut()
    {
        if (ZoomPercent > 50) ZoomPercent -= 10;
    }
}
