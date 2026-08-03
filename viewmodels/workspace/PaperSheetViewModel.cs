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

    private bool _isFlushPending;

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

        if (_owner?.NavPane?.FindReplace != null)
        {
            _owner.NavPane.FindReplace.FindNextAction = (query) =>
            {
                SendToEditor(new BridgeMessage { Type = "FIND_NEXT", Query = query });
            };
            _owner.NavPane.FindReplace.ClearFindAction = () =>
            {
                SendToEditor(new BridgeMessage { Type = "CLEAR_FIND" });
            };
        }
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

                    // Only ack flush if one was explicitly requested via RequestFlushFreeform()
                    if (_isFlushPending)
                    {
                        _isFlushPending = false;
                        FreeformFlushed?.Invoke();
                    }
                    break;
                }
                case "PLAY_AUDIO":
                {
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetViewModel] ===== PLAY_AUDIO HANDLER START =====");
                    Console.WriteLine($"[PaperSheetViewModel] ===== PLAY_AUDIO HANDLER START =====");
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetViewModel] msg.SegId = {msg.SegId}");
                    Console.WriteLine($"[PaperSheetViewModel] msg.SegId = {msg.SegId}");
                    
                    string? segId = msg.SegId;
                    if (segId == null)
                    {
                        Console.WriteLine($"[PaperSheetViewModel] ❌ segId is NULL, aborting");
                        break;
                    }

                    // Parse segId format: "active:42" or "seg_001:10" or just "42"
                    var parts = segId.Split(':');
                    long segmentId = -1;

                    if (parts.Length == 2)
                    {
                        long.TryParse(parts[1], out segmentId);
                        Console.WriteLine($"[PaperSheetViewModel] Parsed format 'prefix:id' → segmentId={segmentId}");
                    }
                    else if (parts.Length == 1)
                    {
                        long.TryParse(parts[0], out segmentId);
                        Console.WriteLine($"[PaperSheetViewModel] Parsed format 'id' → segmentId={segmentId}");
                    }
                    
                    if (segmentId == -1)
                    {
                        Console.WriteLine($"[PaperSheetViewModel] ❌ Failed to parse segmentId, aborting");
                        break;
                    }
                    
                    Console.WriteLine($"[PaperSheetViewModel] ✅ Parsed segmentId={segmentId}, querying repository...");

                    // Query segment to get audio references
                    Console.WriteLine($"[PaperSheetViewModel] Calling GetMergedSegments()...");
                    var all = _workspace.SegmentRepo?.GetMergedSegments();
                    Console.WriteLine($"[PaperSheetViewModel] Got {all?.Count() ?? 0} segments from repository");
                    
                    var seg = all?.FirstOrDefault(s => s.BaseSegment.Id == segmentId);
                    if (seg == null)
                    {
                        Console.WriteLine($"[PaperSheetViewModel] ❌ Segment {segmentId} NOT FOUND in repository");
                        System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: Segment {segmentId} not found in repository");
                        SendToEditor(new BridgeMessage { Type = "AUDIO_UNAVAILABLE", SegId = segId });
                        break;
                    }
                    
                    Console.WriteLine($"[PaperSheetViewModel] ✅ Found segment {segmentId}:");
                    Console.WriteLine($"[PaperSheetViewModel]   AudioSessionId = {seg.BaseSegment.AudioSessionId ?? "NULL"}");
                    Console.WriteLine($"[PaperSheetViewModel]   AudioOffsetMs = {seg.BaseSegment.AudioOffsetMs?.ToString() ?? "NULL"}");
                    Console.WriteLine($"[PaperSheetViewModel]   TsStartMs = {seg.BaseSegment.TsStartMs}");
                    Console.WriteLine($"[PaperSheetViewModel]   TsEndMs = {seg.BaseSegment.TsEndMs}");

                    // Check if segment has audio references (Phase 2 session-based)
                    if (!string.IsNullOrEmpty(seg.BaseSegment.AudioSessionId) && 
                        seg.BaseSegment.AudioOffsetMs.HasValue)
                    {
                        Console.WriteLine($"[PaperSheetViewModel] ✅ Segment has session-based audio, using Phase 2 playback");
                        
                        // Phase 2: Session-based playback
                        string sessionDir = System.IO.Path.Combine(
                            _workspace.Storage.MslcDir, 
                            "audio", 
                            seg.BaseSegment.AudioSessionId);
                        
                        Console.WriteLine($"[PaperSheetViewModel]   SessionDir = {sessionDir}");
                        Console.WriteLine($"[PaperSheetViewModel]   Checking if directory exists...");
                        
                        if (!System.IO.Directory.Exists(sessionDir))
                        {
                            Console.WriteLine($"[PaperSheetViewModel] ❌ Session directory NOT FOUND: {sessionDir}");
                            System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: Session directory not found: {sessionDir}");
                            SendToEditor(new BridgeMessage { Type = "AUDIO_UNAVAILABLE", SegId = segId });
                            break;
                        }
                        
                        Console.WriteLine($"[PaperSheetViewModel] ✅ Session directory exists");

                        long durationMs = seg.BaseSegment.TsEndMs - seg.BaseSegment.TsStartMs;
                        Console.WriteLine($"[PaperSheetViewModel]   Duration = {durationMs}ms");
                        
                        if (durationMs <= 0)
                        {
                            Console.WriteLine($"[PaperSheetViewModel] ❌ Invalid duration {durationMs}ms, aborting");
                            System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: Invalid duration {durationMs}ms");
                            break;
                        }

                        Console.WriteLine($"[PaperSheetViewModel] 🎵 Calling _audioPlayer.PlaySegmentByTime()...");
                        Console.WriteLine($"[PaperSheetViewModel]   segId: {segId}");
                        Console.WriteLine($"[PaperSheetViewModel]   sessionDir: {sessionDir}");
                        Console.WriteLine($"[PaperSheetViewModel]   offsetMs: {seg.BaseSegment.AudioOffsetMs.Value}");
                        Console.WriteLine($"[PaperSheetViewModel]   durationMs: {durationMs}");
                        
                        _audioPlayer?.PlaySegmentByTime(
                            segId, 
                            sessionDir, 
                            seg.BaseSegment.AudioOffsetMs.Value, 
                            durationMs);
                        
                        Console.WriteLine($"[PaperSheetViewModel] ✅ PlaySegmentByTime() call completed");
                    }
                    else
                    {
                        // Fallback: Legacy playback (single WAV file with byte offsets)
                        System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: Segment has no audio_session_id, trying legacy playback");
                        
                        string chunkId = "active";
                        var offsetsPath = _workspace.Storage.GetSegmentOffsetsPath(chunkId);
                        if (!System.IO.File.Exists(offsetsPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: No audio available for this segment");
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

                        long durationMs = seg.BaseSegment.TsEndMs - seg.BaseSegment.TsStartMs;
                        if (durationMs <= 0) break;

                        string wavPath = System.IO.Path.Combine(_workspace.Storage.MslcDir, "segments", $"{chunkId}.audio.wav");
                        if (!System.IO.File.Exists(wavPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[PaperSheet] PLAY_AUDIO: WAV not found at {wavPath}");
                            SendToEditor(new BridgeMessage { Type = "AUDIO_UNAVAILABLE", SegId = segId });
                            break;
                        }

                        _audioPlayer?.PlaySegment(segId, wavPath, byteOffset.Value, durationMs);
                    }
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
                case "FIND_RESULT":
                    if (_owner?.NavPane?.FindReplace != null)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            _owner.NavPane.FindReplace.MatchCount = msg.MatchCount ?? 0;
                            _owner.NavPane.FindReplace.ActiveMatchIndex = msg.ActiveMatchIndex ?? 0;
                        });
                    }
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
        // CAUTION: DEBUG_F18_MOCK is intentionally NOT defined in .csproj (neither Debug nor Release).
        // Activate ONLY by passing /p:DefineConstants=DEBUG_F18_MOCK from CLI for isolated manual UI testing.
        // Never add this symbol to the project's <DefineConstants> or Build Configuration properties.
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
        _isFlushPending = true;
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
