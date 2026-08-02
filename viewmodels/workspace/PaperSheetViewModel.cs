using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Services.Workspace;

namespace MMslcOverlay.ViewModels.Workspace;

public class PaperSheetViewModel : INotifyPropertyChanged
{
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceViewModel _owner;
    private AudioPlayerService? _audioPlayer;

    public Action<MMslcOverlay.Core.Workspace.Models.MergedSegment>? OpenEditDialogAction { get; set; }

    // Action to send messages to the view's WebView2
    public Action<BridgeMessage>? SendToEditorAction { get; set; }

    public Action<string?, string?>? ShowContextMenuAction { get; set; }

    /// <summary>Fired khi JS ack hoàn tất 1 flush đợt freeform (cho FlushPendingAsync).</summary>
    public event Action? FreeformFlushed;

    private int _pendingFreeformWrites;

    public MagicCursorViewModel MagicCursor { get; }
    public ScrollModeController ScrollController { get; } = new ScrollModeController();

    // Gap 5: Mock STT toggle flag
    public bool IsMockSttEnabled { get; set; } = false; // default OFF

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

    public PaperSheetViewModel(WorkspaceService workspace, WorkspaceViewModel owner)
    {
        _workspace = workspace;
        _owner = owner;
        // Magic cursor offset is now managed entirely in the web view, but we can keep the ViewModel if needed
        MagicCursor = new MagicCursorViewModel(() => 0);

        if (_workspace.IngestionService != null)
        {
            _workspace.IngestionService.SegmentAdded += OnSegmentAdded;
        }

        ScrollController.ModeChanged += OnScrollModeChanged;

        _audioPlayer = new AudioPlayerService();
        _audioPlayer.PlaybackStarted += (segId) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SendToEditor(new BridgeMessage { Type = "AUDIO_PLAY_START", SegId = segId });
            });
        };
        _audioPlayer.PlaybackEnded += (segId) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SendToEditor(new BridgeMessage { Type = "AUDIO_PLAY_END", SegId = segId });
            });
        };
    }

    private void OnScrollModeChanged(ScrollMode mode)
    {
        SendToEditor(new BridgeMessage
        {
            Type = "SET_SCROLL_MODE",
            Mode = mode == ScrollMode.WatchMagicCursor ? "WATCH_MAGIC" : "FREE_INPUT"
        });
    }

    public void CommitSegmentEdit(MMslcOverlay.Core.Workspace.Models.MergedSegment original, string newTextSrc, string? newTextTrs)
    {
        if (_workspace.UserDataRepo == null) return;

        var session = new SegmentEditSession(original, _workspace.UserDataRepo);
        session.CommitEdit(newTextSrc, newTextTrs);
        _owner?.MarkDirty();

        if (original.TextSrc != newTextSrc)
        {
            SendToEditor(new BridgeMessage
            {
                Type = "APPLY_PATCH",
                SegId = original.BaseSegment.Id.ToString(),
                Field = "TextSrc",
                NewValue = newTextSrc
            });
        }

        if (!string.IsNullOrEmpty(newTextTrs) && original.TextTrs != newTextTrs)
        {
            SendToEditor(new BridgeMessage
            {
                Type = "APPLY_PATCH",
                SegId = original.BaseSegment.Id.ToString(),
                Field = "TextTrs",
                NewValue = newTextTrs
            });
        }
    }

    public void SendToEditor(BridgeMessage msg)
    {
        SendToEditorAction?.Invoke(msg);
    }

    public void HandleWebMessage(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<BridgeMessage>(json);
            if (msg == null) return;

            switch (msg.Type)
            {
                case "DOCUMENT_READY":
                    LoadInitialState();
                    break;
                case "FREEFORM_CHANGED":
                {
                    if (_workspace.UserDataRepo == null) break;

                    string? blockIdStr = msg.BlockId;
                    string? anchorAfter = msg.AnchorAfter;
                    string content = msg.Content ?? string.Empty;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    if (string.IsNullOrEmpty(blockIdStr))
                    {
                        var block = new FreeformBlock
                        {
                            AnchorAfter = anchorAfter,
                            Content = content,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        long newId = _workspace.UserDataRepo.InsertFreeformBlock(block);

                        var ack = new BridgeMessage
                        {
                            Type = "FREEFORM_PERSISTED",
                            AnchorAfter = anchorAfter,
                            BlockId = newId.ToString()
                        };
                        SendToEditor(ack);
                    }
                    else if (long.TryParse(blockIdStr, out long blockId))
                    {
                        var block = new FreeformBlock
                        {
                            Id = blockId,
                            AnchorAfter = anchorAfter,
                            Content = content,
                            UpdatedAt = now
                        };
                        _workspace.UserDataRepo.UpdateFreeformBlock(block);
                    }

                    _pendingFreeformWrites = 0;
                    FreeformFlushed?.Invoke();
                    break;
                }
                case "PLAY_AUDIO":
                {
                    System.Diagnostics.Debug.WriteLine($"Play audio for seg {msg.SegId}");
                    string? segId = msg.SegId;
                    if (segId == null) break;

                    // Parse segId format: "active:42" or "seg_001:10" or just "42"
                    var parts = segId.Split(':');
                    string chunkId = "active";
                    long segmentId = -1;

                    if (parts.Length == 2)
                    {
                        chunkId = parts[0];
                        long.TryParse(parts[1], out segmentId);
                    }
                    else if (parts.Length == 1)
                    {
                        long.TryParse(parts[0], out segmentId);
                    }
                    if (segmentId == -1) break;

                    // Gap 10 fix: Guard against missing offsets file
                    var offsetsPath = _workspace.Storage.GetSegmentOffsetsPath(chunkId);
                    if (!System.IO.File.Exists(offsetsPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: Offsets file not found at {offsetsPath}. No recording yet?");
                        SendToEditor(new BridgeMessage { Type = "AUDIO_UNAVAILABLE", SegId = segId });
                        break;
                    }

                    var offsetIndex = new MMslcOverlay.Core.Workspace.Storage.AudioOffsetIndex(offsetsPath);
                    long? byteOffset = offsetIndex.GetOffset(segmentId);
                    if (!byteOffset.HasValue)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: Offset not found for segment {segmentId}");
                        SendToEditor(new BridgeMessage { Type = "AUDIO_UNAVAILABLE", SegId = segId });
                        break;
                    }

                    var all = _workspace.SegmentRepo?.GetMergedSegments();
                    var seg = all?.FirstOrDefault(s => s.BaseSegment.Id == segmentId);
                    if (seg == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: Segment {segmentId} not found in repository");
                        break;
                    }

                    long durationMs = seg.BaseSegment.TsEndMs - seg.BaseSegment.TsStartMs;
                    if (durationMs <= 0) break;

                    // Gap 10 fix: Guard against missing WAV file
                    string wavPath = System.IO.Path.Combine(_workspace.Storage.MslcDir, "segments", $"{chunkId}.audio.wav");
                    if (!System.IO.File.Exists(wavPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: WAV not found at {wavPath}. No recording yet?");
                        SendToEditor(new BridgeMessage { Type = "AUDIO_UNAVAILABLE", SegId = segId });
                        break;
                    }

                    _audioPlayer?.PlaySegment(segId, wavPath, byteOffset.Value, durationMs);
                    break;
                }
                case "OPEN_EDIT_FIELD":
                    System.Diagnostics.Debug.WriteLine($"Open edit field for seg {msg.SegId}");
                    if (msg.SegId != null)
                    {
                        var segments = _workspace.SegmentRepo?.GetMergedSegments();
                        var seg = segments?.FirstOrDefault(s => s.BaseSegment.Id.ToString() == msg.SegId);
                        if (seg != null)
                        {
                            OpenEditDialogAction?.Invoke(seg);
                        }
                    }
                    break;
                case "SCROLL_MODE_CHANGED":
                    System.Diagnostics.Debug.WriteLine($"Scroll mode changed: {msg.Mode}");
                    // Sync with C# controller if needed
                    break;
                case "MAGIC_CURSOR_MOVED":
                    System.Diagnostics.Debug.WriteLine($"Magic cursor moved to {msg.Pos}");
                    break;
                case "SHOW_CONTEXT_MENU":
                    System.Diagnostics.Debug.WriteLine($"Show context menu: {msg.MenuType} {msg.TargetId}");
                    ShowContextMenuAction?.Invoke(msg.MenuType ?? "Unknown", msg.TargetId ?? "");
                    break;
                case "JS_ERROR":
                    Console.WriteLine($"[JS_ERROR] {json}");
                    System.Diagnostics.Debug.WriteLine($"[JS_ERROR] {json}");
                    break;
                case "JS_DEBUG":
                    Console.WriteLine($"[JS_DEBUG] {json}");
                    System.Diagnostics.Debug.WriteLine($"[JS_DEBUG] {json}");
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PaperSheetViewModel Error handling web message: " + ex.Message);
        }
    }

    private void LoadInitialState()
    {
        var allSegments = _workspace.SegmentRepo?.GetMergedSegments()?.ToList();
        var msg = new BridgeMessage
        {
            Type = "LOAD_DOCUMENT",
            Segments = new List<BridgeSegment>()
        };

#if DEBUG_F18_MOCK
        if (allSegments == null || allSegments.Count == 0)
        {
            allSegments = new List<MMslcOverlay.Core.Workspace.Models.MergedSegment>
            {
                new MMslcOverlay.Core.Workspace.Models.MergedSegment(new Segment { Id = 101, TsStartMs = 0, TsEndMs = 5000, SpeakerId = "SPK_1", TextSrc = "Welcome to the m-mslc-overlay test." }),
                new MMslcOverlay.Core.Workspace.Models.MergedSegment(new Segment { Id = 102, TsStartMs = 5500, TsEndMs = 8000, SpeakerId = "SPK_2", TextSrc = "This is a mock machine segment." }),
                new MMslcOverlay.Core.Workspace.Models.MergedSegment(new Segment { Id = 103, TsStartMs = 8200, TsEndMs = 12000, SpeakerId = "SPK_1", TextSrc = "It helps verify the F18 specification for ProseMirror." })
            };
        }
#endif
        // Production: empty workspace renders an empty document ready for real-time STT ingestion.

        if (allSegments != null)
        {
            foreach (var seg in allSegments)
            {
                msg.Segments.Add(new BridgeSegment
                {
                    SegId = seg.BaseSegment.Id.ToString(),
                    TsStartMs = seg.BaseSegment.TsStartMs,
                    TsEndMs = seg.BaseSegment.TsEndMs,
                    SpeakerId = string.IsNullOrEmpty(seg.BaseSegment.SpeakerId) ? "UNK" : seg.BaseSegment.SpeakerId,
                    TextSrc = seg.TextSrc,
                    TextTrs = seg.TextTrs
                });
            }
        }

        var blocks = _workspace.UserDataRepo?.GetAllFreeformBlocks();
        msg.FreeformBlocks = new List<BridgeFreeformBlock>();
        if (blocks != null)
        {
            foreach (var b in blocks)
            {
                msg.FreeformBlocks.Add(new BridgeFreeformBlock
                {
                    BlockId = b.Id.ToString(),
                    AnchorAfter = b.AnchorAfter,
                    Content = b.Content
                });
            }
        }

        SendToEditor(msg);

#if DEBUG_F18_MOCK
        if (IsMockSttEnabled)
        {
            StartMockLiveSTTInjection();
        }
#endif
    }

    public void StartMockLiveSTTInjection()
    {
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        long mockSegId = 200;
        timer.Tick += (s, e) => 
        {
            mockSegId++;
            var liveSeg = new Segment 
            { 
                Id = mockSegId, 
                TsStartMs = mockSegId * 1000, 
                TsEndMs = mockSegId * 1000 + 2000, 
                SpeakerId = "LIVE_SPK", 
                TextSrc = $"Live STT segment {mockSegId} arrived..." 
            };
            OnSegmentAdded(liveSeg);
            if (mockSegId > 210) timer.Stop();
        };
        timer.Start();
    }

    private void OnSegmentAdded(Segment segment)
    {
        var msg = new BridgeMessage
        {
            Type = "INSERT_MACHINE_SEGMENT",
            SegId = segment.Id.ToString(),
            TsStartMs = segment.TsStartMs,
            TsEndMs = segment.TsEndMs,
            SpeakerId = string.IsNullOrEmpty(segment.SpeakerId) ? "UNK" : segment.SpeakerId,
            TextSrc = segment.TextSrc
            // No trs yet from ingestion
        };

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SendToEditor(msg);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ─── UI Actions ────────────────────────────────────────────────────────
    public void RequestFlushFreeform()
    {
        _pendingFreeformWrites = 1;
        SendToEditor(new BridgeMessage { Type = "FLUSH_FREEFORM" });
    }

    public void ZoomIn()
    {
        if (ZoomPercent < 300) ZoomPercent += 10;
    }

    public void ZoomOut()
    {
        if (ZoomPercent > 50) ZoomPercent -= 10;
    }
}
