using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using m_mslc_overlay.views.components;
using m_mslc_overlay.views.overlay;
using m_mslc_overlay.services;
using m_mslc_overlay.core;
using MMslcOverlay.Services;
using MMslcOverlay.ViewModels.Workspace;
using MMslcOverlay.Core.Workspace.Models;

namespace m_mslc_overlay
{
    public partial class MainWindow : Window
    {
        private FloatingTextOverlay? _currentOverlay;
        private AppContainerHiderService _hiderService;
        private LiveCaptionPipeService _pipeService;
        private InjectorService _injectorService;
        private AIService _aiService;
        private ShortSentenceBuffer _shortSentenceBuffer;
        private readonly SegmentTracker _segmentTracker = new SegmentTracker();
        private readonly SegmentDisplayModel _segmentDisplayModel = new SegmentDisplayModel();
        private readonly VisualStateMapper _visualStateMapper;
        private readonly RevisionWindowService _revisionWindow = new RevisionWindowService();
        private readonly m_mslc_overlay.core.Animation.FadeAnimationController _fadeAnimationController;
        private SystemMonitor _sysMonitor;
        private DispatcherTimer _resourceTimer;
        private DispatcherTimer _uiUpdateTimer;
        private HotkeyManager? _hotkeyManager;
        private FocusKeyController? _focusKeyController;

        private bool _isTranslationEnabled = true;
        private string _contextTopic = "Game/Phim";
        
        private DiarizerProcessManager? _diarizerManager;
        private string _latestSpeakerUid = string.Empty;
        private string _latestSpeakerDisplayName = string.Empty;
        private readonly System.Collections.Generic.Dictionary<Guid, long> _segmentIdMap = new();

        private readonly object _translationLock = new object();
        private string _translationBuffer = "";
        private string _translationDisplayBuffer = "";
        private bool _isTranslationDirty = false;
        
        // CRITICAL-TEXT-001: Track previous segment end time and utterance boundary for continuous timeline
        private long _lastSegmentEndMs = 0;
        private ulong _lastUtteranceOffset = 0;  // tracks utterance boundary to detect new utterance vs intra-utterance commit
        
        // FIX V6: Per-segment translation with context hints
        // Translate EACH segment individually (for subtitle export compatibility)
        // but provide previous segments as context hints to improve quality
        private readonly System.Collections.Generic.Queue<string> _contextHints = new();
        private const int MaxContextHints = 3;  // Keep last 3 segments as context

        private readonly object _logLock = new object();
        private readonly System.Collections.Generic.List<string> _rawLogs = new System.Collections.Generic.List<string>();
        private bool _isLogDirty = false;

        private string _lastPartialCaption = "";
        private bool _isPartialCaptionDirty = false;

        private enum HookState
        {
            Waiting,
            Detected,
            Injected,
            Failed
        }
        private HookState _currentHookState = HookState.Waiting;
        private bool _isAdjustingSidebar = false;
        private double _userSidebarWidth = 240.0;

        // Workspace ViewModel — DataContext của MainWindow, quản lý toàn bộ session lifecycle
        private readonly WorkspaceViewModel _workspaceVm = new();
        // Guard để tránh double-subscribe PaperSheetView.ExportRequested khi workspace reopen
        private bool _paperSheetExportWired = false;

        public MainWindow()
        {
            LoggerService.Initialize();
            InitializeComponent();

            // MainWindow IS the workspace container — set DataContext ngay sau InitializeComponent
            DataContext = _workspaceVm;

            // Wire export picker callback cho WorkspaceViewModel
            _workspaceVm.RequestSavePathAction = ShowSaveFileDialogAsync;
            _workspaceVm.OpenFileExternallyAction = OpenFileInDefaultApp;
            _workspaceVm.ImportScriptRequested += OnImportScriptRequested;

            // R1 fix: reflect session file list changes in real-time (after recording stop, export, etc.)
            _workspaceVm.SessionFiles.CollectionChanged += (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(RefreshSessionFilesList);

            _workspaceVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(WorkspaceViewModel.IsOpen))
                {
                    var idlePlaceholder = this.FindControl<Border>("WorkspaceIdlePlaceholder");
                    var paperSheet = this.FindControl<MMslcOverlay.Views.Workspace.PaperSheetView>("WorkspacePaperSheet");
                    if (idlePlaceholder != null) idlePlaceholder.IsVisible = !_workspaceVm.IsOpen;
                    if (paperSheet != null)
                    {
                        paperSheet.IsVisible = _workspaceVm.IsOpen;
                        // Thay đổi DataContext của PaperSheetView thành WorkspaceViewModel thay vì Sheet
                        // Vì PaperSheetView giờ đây là Composite Root bao gồm cả Toolbars (cần WorkspaceViewModel)
                        if (_workspaceVm.IsOpen) paperSheet.DataContext = _workspaceVm;

                        // Wire SubToolbar export → MainWindow handler (once, idempotent via flag)
                        if (_workspaceVm.IsOpen && !_paperSheetExportWired)
                        {
                            paperSheet.ExportRequested += async (payload) =>
                                await ProcessAdvancedExportPayloadAsync(payload);
                            _paperSheetExportWired = true;
                        }
                    }

                    // Bug 2 fix: Khi workspace vừa open, sync trạng thái diarizer vào NavPane ngay.
                    // Nếu diarizer chưa start → hiện fallback panel thay vì blank speaker list.
                    if (_workspaceVm.IsOpen)
                        SyncNavPaneDiarizerState();
                }
                else if (e.PropertyName == nameof(WorkspaceViewModel.DisplayName) ||
                         e.PropertyName == nameof(WorkspaceViewModel.WorkspacePath) ||
                         e.PropertyName == nameof(WorkspaceViewModel.LastModifiedDisplay))
                {
                    UpdateSessionDisplay();
                }
            };


            _hiderService = new AppContainerHiderService();
            _pipeService = new LiveCaptionPipeService();
            _injectorService = new InjectorService();
            _aiService = new AIService();
            _shortSentenceBuffer = new ShortSentenceBuffer();
            _visualStateMapper = new VisualStateMapper(_segmentDisplayModel, _segmentTracker);
            _fadeAnimationController = new m_mslc_overlay.core.Animation.FadeAnimationController(_revisionWindow);

            // ATOM50: Short sentence buffer merges fragments (≤3 words) with the next
            // long sentence before forwarding to translation, avoiding wasteful API calls
            // for isolated tokens like "but", "So", "Because", "I".
            // FIX V3: Now receives full CommitMetadata to preserve UtteranceOffset for correct linking.
            // FIX V6: Per-segment translation with context hints
            _shortSentenceBuffer.OnFlush += (mergedMeta) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    if (IsTranslationEnabled)
                    {
                        lock (_translationLock) {
                            _translationBuffer = "";
                            
                            // V6: Build context-aware translation prompt
                            // Format: "[Previous context...] >> CURRENT_TEXT"
                            // This tells translation engine about context without merging translations
                            string contextPrefix = "";
                            if (_contextHints.Count > 0)
                            {
                                contextPrefix = string.Join(" ", _contextHints) + " >> ";
                            }
                            
                            string textWithContext = contextPrefix + mergedMeta.Text;
                            
                            // Add current text to context hints for next translation
                            _contextHints.Enqueue(mergedMeta.Text);
                            while (_contextHints.Count > MaxContextHints)
                            {
                                _contextHints.Dequeue();
                            }
                            
                            // Clear context on HardCommit (sentence boundary)
                            if (mergedMeta.Reason == "HardCommit")
                            {
                                _contextHints.Clear();
                            }
                        }
                        
                        // Pass to translation with UtteranceOffset preserved for linking
                        _aiService.EnqueueTranslation(mergedMeta);
                        
                        System.Diagnostics.Debug.WriteLine(
                            $"[V6] Translating segment with {_contextHints.Count - 1} context hints: '{mergedMeta.Text}'");
                    }
                    else
                    {
                        if (_currentOverlay != null && _currentOverlay.IsVisible)
                        {
                            if (_currentOverlay.UseTypewriter)
                                _currentOverlay.EnqueueText(mergedMeta.Text);
                            else
                                _currentOverlay.AddFinalText(mergedMeta.Text);
                        }
                    }
                });
            };
            
            _sysMonitor = new SystemMonitor();
            _resourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _resourceTimer.Tick += OnResourceTimerTick;
            _resourceTimer.Start();
            
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiUpdateTimer.Tick += OnUiUpdateTimerTick;
            _uiUpdateTimer.Start();
            
            _aiService.ContextTopic = _contextTopic;

            // Run startup bootstrap to query environment & update status pane immediately
            _ = InitBootstrapAsync();

            // ATOM80: log when a revision (hot-replace) occurs
            _revisionWindow.OnRevise += (prev, merged) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [ATOM80 REVISE] «{prev}» → «{merged}»\n");
            };

            // 1. Nhận luồng text thô partial (đang nhận dạng) từ Extractor
            _pipeService.OnPartialCaptionReceived += (txt) => {
                _lastPartialCaption = txt;
                _isPartialCaptionDirty = true;
                // Phase 1 anchor: snapshot recorder offset at first non-empty partial,
                // before any commit fires. This captures the true audio file position of
                // utterance onset without back-calculating from SDK-reported duration.
                if (!string.IsNullOrWhiteSpace(txt))
                    _workspaceVm.Service?.AudioRecorder?.SnapshotOffsetAtFirstPartial();
            };

            // 2. Nhận câu thô hoàn chỉnh (final) từ Extractor
            // NOTE: OnFinalSentenceReceived fires on the pipe background thread.
            // We marshal to the UI thread first because downstream consumers
            // (SegmentTracker → VisualStateMapper → SegmentDisplayModel) may interact
            // with Avalonia objects (e.g. SolidColorBrush) that must be on the UI thread.
            _pipeService.OnFinalSentenceReceived += (meta) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    if (string.IsNullOrWhiteSpace(meta.Text)) return;

                    // Định dạng timestamp cho câu thô
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    
                    int avgSS = (int)_pipeService.AverageSpeechSpeed;
                    string flagAvgSS = $"[AvgSS:{avgSS}ms]";
                    string danglingFlag = meta.IsDangling ? " [⚠ DANGLING]" : "";
                    string mergedFlag = meta.WasMerged ? " [MERGED]" : "";

                    string logLine = $"[{timestamp}] {flagAvgSS}{danglingFlag}{mergedFlag} {LanguageManager.GetString("Log_EnglishPrefix")}: {meta.Text}\n";
                    AppendLog(logLine);

                    // ATOM79: track segment lifecycle
                    var segment = _segmentTracker.TrackCommit(meta);

                    // Ingest into workspace if open
                    if (_workspaceVm.IsOpen && _workspaceVm.Service?.IngestionService != null)
                    {
                        // DISABLED: AUTO-START RECORDING (user must click Record button explicitly)
                        // Reason: Recording should be under explicit user control, not automatic
                        // if (!_workspaceVm.IsRecording)
                        // {
                        //     _workspaceVm.StartRecording();
                        //     System.Diagnostics.Debug.WriteLine("[MainWindow] 🎙️ Auto-started recording on first STT segment");
                        // }
                        
                        // CRITICAL-TEXT-001 FIX: Handle timestamp calculation
                        long tsEndMs;
                        
                        if (meta.AcousticEndMs > 0)
                        {
                            // Use acoustic timestamp from SDK (relative time in ms)
                            tsEndMs = (long)meta.AcousticEndMs;
                        }
                        else
                        {
                            // Fallback: Use previous segment's end + estimate
                            // NEVER use Unix timestamp - it causes overflow!
                            if (_lastSegmentEndMs > 0)
                            {
                                tsEndMs = _lastSegmentEndMs + 2000; // Estimate 2s from previous
                            }
                            else
                            {
                                // Very first segment with no acoustic data: start at 0
                                tsEndMs = 2000;
                            }
                        }
                        
                        // tsStartMs selection logic:
                        // - New utterance  (utteranceOffset changed): use SDK utterance start directly.
                        //   Reason: the new utterance genuinely starts at a different audio position;
                        //   using _lastSegmentEndMs would push audioStart past actual speech onset.
                        // - Same utterance (utteranceOffset unchanged): use _lastSegmentEndMs.
                        //   Reason: multiple commits within one utterance must be sequential;
                        //   utteranceOffset stays frozen at utterance start for all of them.
                        long startOffsetMs = (long)(meta.UtteranceOffset / 10000);
                        bool isNewUtterance = meta.UtteranceOffset != _lastUtteranceOffset;
                        long tsStartMs;
                        if (isNewUtterance && startOffsetMs > 0)
                        {
                            tsStartMs = startOffsetMs;  // cross-utterance: trust SDK offset
                        }
                        else
                        {
                            tsStartMs = _lastSegmentEndMs > 0 ? _lastSegmentEndMs : startOffsetMs;  // intra-utterance: chain from previous commit end
                        }
                        _lastUtteranceOffset = meta.UtteranceOffset;
                        if (tsEndMs < tsStartMs) tsEndMs = tsStartMs; // Enforce monotonicity on end only
                        _lastSegmentEndMs = tsEndMs;  // Update for next segment
                        
                        // Wire atom32 speaker identification into segment ingestion and sync with Doc Nav
                        string resolvedSpeaker = !string.IsNullOrEmpty(meta.SpeakerId) ? meta.SpeakerId : (!string.IsNullOrEmpty(_latestSpeakerUid) ? _latestSpeakerUid : "UNK");
                        if (resolvedSpeaker != "UNK")
                        {
                            string dispName = (resolvedSpeaker == _latestSpeakerUid && !string.IsNullOrEmpty(_latestSpeakerDisplayName)) ? _latestSpeakerDisplayName : resolvedSpeaker;
                            _workspaceVm.NavPane?.AddOrUpdateSpeaker(resolvedSpeaker, dispName);
                        }
                        

                        long dbId = _workspaceVm.Service.IngestionService.IngestSttPayload(
                            tsStartMs: tsStartMs,
                            tsEndMs: tsEndMs,
                            textSrc: meta.Text,
                            textTrs: null,
                            speakerId: resolvedSpeaker,
                            commitType: meta.Reason == "HardCommit" ? "HARD" : "SOFT",
                            // CRITICAL-TEXT-001: Pass acoustic metadata to database
                            acousticEndMs: meta.AcousticEndMs,
                            utteranceOffset: meta.UtteranceOffset,
                            isDangling: meta.IsDangling,
                            avgSpeechSpeedMs: (int)_pipeService.AverageSpeechSpeed,
                            commitReason: meta.Reason
                        );
                        // Stamp dbId directly on meta so OnTranslationCompleted can use it
                        // without going through _segmentIdMap (which drifts on ShortSentenceBuffer merges)
                        meta.WorkspaceDbId = dbId;
                        _segmentIdMap[segment.Id] = dbId;
                    }

                    // FIX V6: No accumulation needed - translate per segment immediately
                    // Context hints are managed in OnFlush handler

                    // ATOM50: route through ShortSentenceBuffer — translation fires
                    // only when OnFlush is triggered (either by merge or timeout).
                    // FIX V3: Pass full CommitMetadata to preserve UtteranceOffset
                    _shortSentenceBuffer.Feed(meta);
                });
            };

            // 3. Nhận các token dịch từ AI
            _aiService.OnTranslationTokenReceived += (tokenStr) => {
                // Kiểm tra UI thread không lock, chỉ set cờ
                bool isTranslated = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    isTranslated = IsTranslationEnabled;
                    if (isTranslated)
                    {
                        lock(_translationLock) {
                            _translationBuffer += tokenStr;
                            _translationDisplayBuffer = _translationBuffer;
                        }
                        _isTranslationDirty = true;
                    }
                });
            };

            // 4. Khi hoàn thành dịch 1 câu hoàn chỉnh
            // ATOM81: handler now receives TranslationResult (includes source CommitMetadata)
            _aiService.OnTranslationCompleted += (result) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    if (IsTranslationEnabled)
                    {
                        string fullSentence = result.Translation;
                        // In bản dịch tiếng Việt sang RawTextLog để dễ debug song song
                        string timestamp = DateTime.Now.ToString("HH:mm:ss");
                        string errFlag = result.IsError ? " [ERR]" : "";
                        string logLine = $"[{timestamp}]{errFlag} {LanguageManager.GetString("Log_VietnamesePrefix")}: {fullSentence}\n-----------------------------------\n";
                        AppendLog(logLine);

                        // ATOM79: link translation back to originating segment
                        var linkedSeg = _segmentTracker.LinkTranslation(result);

                        // ✅ FIX 1: Add explicit null check with logging
                        if (linkedSeg == null)
                        {
                            AppendLog($"[{timestamp}] ⚠️ WARNING: Translation '{fullSentence}' could not be linked to any segment. Data loss risk!\n");
                            System.Diagnostics.Debug.WriteLine($"[MainWindow] ⚠️ LinkedSeg is NULL for translation: {fullSentence}");
                            // Continue to overlay display, but don't try to save to workspace
                        }

                        // Cập nhật bản dịch vào Workspace database và gửi sang WebView2 PaperSheet
                        if (linkedSeg != null && _workspaceVm.IsOpen && _workspaceVm.Service?.ActiveSegmentRepo != null)
                        {
                            // Prefer WorkspaceDbId stamped directly on Source meta (immune to _segmentIdMap drift from ShortSentenceBuffer merges).
                            // Fallback to _segmentIdMap for backward compat when Source is null.
                            long dbId = -1;
                            if (result.Source?.WorkspaceDbId >= 0)
                            {
                                dbId = result.Source.WorkspaceDbId;
                            }
                            else if (_segmentIdMap.TryGetValue(linkedSeg.Id, out long mappedId))
                            {
                                dbId = mappedId;
                            }
                            
                            if (dbId >= 0)
                            {
                                // FIX V6: Store per-segment translation (for subtitle export)
                                // Translation is for THIS segment only, not accumulated
                                
                                // 1. Lưu vào SQLite database
                                _workspaceVm.Service.ActiveSegmentRepo.UpdateSegmentTranslation(dbId, fullSentence);

                                // 2. Đẩy sang WebView2 PaperSheet UI
                                if (_workspaceVm.Sheet is PaperSheetViewModel paperSheetVm)
                                {
                                    paperSheetVm.SendToEditor(new BridgeMessage
                                    {
                                        Type = "APPLY_PATCH",
                                        SegId = dbId.ToString(),
                                        Field = "TextTrs",
                                        NewValue = fullSentence
                                    });
                                }
                                
                                System.Diagnostics.Debug.WriteLine(
                                    $"[V6] Applied translation to segment {dbId}: " +
                                    $"'{fullSentence.Substring(0, Math.Min(40, fullSentence.Length))}{(fullSentence.Length > 40 ? "..." : "")}'");
                            }
                            else
                            {
                                // ✅ FIX 2: Add logging for missing DB ID
                                AppendLog($"[{timestamp}] ⚠️ WARNING: No dbId found for translation. Translation lost!\n");
                                System.Diagnostics.Debug.WriteLine($"[MainWindow] ⚠️ No dbId for translation. Source.WorkspaceDbId={result.Source?.WorkspaceDbId}, linkedSeg.Id={linkedSeg.Id}");
                            }
                        }

                        if (_currentOverlay != null && _currentOverlay.IsVisible)
                        {
                            // ATOM80: Check if this translation should replace the previous short one
                            bool wasRevised = _revisionWindow.TryRevise(result);

                            if (wasRevised)
                            {
                                // Hot-replace: replace last displayed translation instead of appending
                                _currentOverlay.ReplaceLastText(fullSentence);
                            }
                            else if (ConfigManager.Current.TranslationEngine == "DeepL API")
                            {
                                if (_currentOverlay.UseTypewriter)
                                {
                                    _currentOverlay.StartTypewriterPump();
                                    _currentOverlay.EnqueueText(fullSentence + " ");
                                }
                                else
                                {
                                    _currentOverlay.AddFinalText(fullSentence);
                                }
                            }
                            else
                            {
                                // Streaming engines fallback
                                _currentOverlay.AddFinalText(fullSentence);
                            }

                            // ATOM79: mark as rendered after overlay receives text
                            if (linkedSeg != null)
                                _segmentTracker.MarkRendered(linkedSeg);

                            // ATOM80: after rendering, notify RevisionWindow to open window if this is a short translation
                            _revisionWindow.OnTranslationRendered(result);
                        }
                    }
                });
            };

            // 5. Cập nhật log trạng thái của Pipe Server
            _pipeService.OnStatusChanged += (statusMsg) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [SYSTEM] {statusMsg}\n");
                
                // ATOM50: reset buffer on new pipe session to avoid stale pending
                if (statusMsg.Contains("Client connected"))
                {
                    _shortSentenceBuffer.Reset();
                    _segmentTracker.Reset();  // ATOM79: clear stale segments on reconnect
                    _revisionWindow.Reset();  // ATOM80: clear pending revision window on reconnect
                    _segmentIdMap.Clear();
                    _lastSegmentEndMs = 0;     // CRITICAL-TEXT-001: Reset timing state on reconnect
                    _lastUtteranceOffset = 0;  // Reset utterance boundary tracker on reconnect
                    
                    // FIX V6: Clear context hints on reconnect
                    lock (_translationLock)
                    {
                        _contextHints.Clear();
                    }
                }
                
                // DISABLED: AUTO-STOP RECORDING (user must click Stop button explicitly)
                // Reason: Recording should persist across STT disconnect/reconnect
                // if (statusMsg.Contains("Client disconnected"))
                // {
                //     if (_workspaceVm.IsRecording)
                //     {
                //         _workspaceVm.StopRecording();
                //         System.Diagnostics.Debug.WriteLine("[MainWindow] 🛑 Auto-stopped recording on STT disconnect");
                //     }
                // }
                
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            };

            // 6. Cập nhật log lỗi của Pipe Server
            _pipeService.OnError += (errorMsg) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [ERROR] {errorMsg}\n");
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            };

            this.Closing += async (s, e) => {
                // Đóng khi có workspace: flush pending + confirm nếu dirty
                if (_workspaceVm.IsOpen || _workspaceVm.IsDirty)
                {
                    e.Cancel = true; // Cancel để xử lý async

                    if (_workspaceVm.IsDirty)
                    {
                        var choice = await ShowUnsavedChangesDialogAsync();
                        if (choice == UnsavedChoice.Cancel) return; // Stay open
                        if (choice == UnsavedChoice.Save)
                            await FlushPendingEditsAsync();
                    }

                    if (_workspaceVm.IsRecording) _workspaceVm.StopRecording();

                    // Sau flush, thực hiện cleanup rồi close
                    if (_workspaceVm.IsOpen)
                        _workspaceVm.CloseWorkspace();

                    CleanupServices();
                    this.Close(); // Close thực sự sau khi xử lý xong
                }
                else
                {
                    CleanupServices();
                }
            };

            this.Opened += async (s, e) => {
                InitializeHotkeys();
                InitializeFocusKeys();
                // Bug 1: Pre-warm diarizer at app open if enabled, so model is loaded
                // before user hits Start Session (eliminates the first-session cold-start delay).
                if (ConfigManager.Current.EnableDiarizer)
                {
                    await PreWarmDiarizerAsync();
                }
            };

            // Dò tìm PID lúc khởi động (nếu đã bật sẵn Live Captions)
            DetectTargetProcess();

            // Khởi chạy Offline Translation Server nếu được cấu hình làm Engine dịch chính
            if (ConfigManager.Current.TranslationEngine == "Offline CTranslate2")
            {
                LoggerService.Log("[MainWindow] Offline Translation Engine is active. Starting offline server...");
                if (Uri.TryCreate(ConfigManager.Current.OfflineTranslateUrl, UriKind.Absolute, out var uri))
                {
                    OfflineTranslationServerManager.ServerPort = uri.Port;
                }
                _ = OfflineTranslationServerManager.StartServerAsync();
            }

            // Register responsive layout events
            this.SizeChanged += MainWindow_SizeChanged;
            var sidebar = this.FindControl<Border>("SidebarBorder");
            if (sidebar != null)
            {
                sidebar.SizeChanged += SidebarBorder_SizeChanged;
            }
            // TranscriptViewport was removed — NavPane wiring removed with it
        }

        /// <summary>
        /// Overload constructor: mở MainWindow mới với một workspace path đã chọn.
        /// Dùng cho VS Code pattern: close current + open new.
        /// </summary>
        public MainWindow(string workspacePath) : this()
        {
            _workspaceVm.OpenOrCreate(workspacePath);
        }

        /// <summary>
        /// Kết quả dialog Unsaved Changes.
        /// </summary>
        private enum UnsavedChoice { Save, DontSave, Cancel }

        /// <summary>
        /// Hiển thị dialog Save / Don't Save / Cancel.
        /// </summary>
        private async System.Threading.Tasks.Task<UnsavedChoice> ShowUnsavedChangesDialogAsync()
        {
            if (!_workspaceVm.IsDirty) return UnsavedChoice.Save;

            var tcs = new System.Threading.Tasks.TaskCompletionSource<UnsavedChoice>();
            var dialog = new Window
            {
                Title = "Unsaved Changes",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new Border
                {
                    Padding = new Avalonia.Thickness(24),
                    Child = new StackPanel
                    {
                        Spacing = 16,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Workspace '{_workspaceVm.WorkspaceName}' có thay đổi chưa được lưu.\nBạn muốn làm gì trước khi tiếp tục?",
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                Foreground = Brushes.White
                            },
                            new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                Spacing = 8,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                Children =
                                {
                                    new Button { Content = "Lưu", Tag = UnsavedChoice.Save, Width = 90 },
                                    new Button { Content = "Không lưu", Tag = UnsavedChoice.DontSave, Width = 90 },
                                    new Button { Content = "Hủy", Tag = UnsavedChoice.Cancel, Width = 90 }
                                }
                            }
                        }
                    }
                }
            };

            foreach (var child in ((StackPanel)((Border)dialog.Content!).Child!).Children)
            {
                if (child is StackPanel btnRow)
                {
                    foreach (var btn in btnRow.Children.OfType<Button>())
                    {
                        btn.Click += (_, _) =>
                        {
                            tcs.TrySetResult((UnsavedChoice)btn.Tag!);
                            dialog.Close();
                        };
                    }
                }
            }

            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private void OnNewWorkspaceMenuClick(object? sender, RoutedEventArgs e)
        {
            _ = NewWorkspaceFlowAsync();
        }

        private void OnExportAdvancedMenuClick(object? sender, RoutedEventArgs e)
        {
            _ = ShowAdvancedExportDialogAsync();
        }

        private async System.Threading.Tasks.Task ShowAdvancedExportDialogAsync()
        {
            if (!_workspaceVm.IsOpen)
            {
                await m_mslc_overlay.views.dialogs.MessageDialog.ShowAsync(
                    this, "Thông báo", "Vui lòng mở một workspace trước khi xuất file.");
                return;
            }

            var exportDialog = new m_mslc_overlay.views.dialogs.ExportDialog(
                async (jsonPayload) => await ProcessAdvancedExportPayloadAsync(jsonPayload)
            );
            await exportDialog.ShowDialog(this);
        }

        private async System.Threading.Tasks.Task ProcessAdvancedExportPayloadAsync(string jsonPayload)
        {
            if (_workspaceVm.Service?.SegmentRepo == null)
            {
                await m_mslc_overlay.views.dialogs.MessageDialog.ShowAsync(
                    this, "Lỗi xuất file", "Workspace chưa có dữ liệu phân đoạn để xuất.");
                return;
            }

            var loadingDialog = new m_mslc_overlay.views.dialogs.LoadingDialog();
            _ = loadingDialog.ShowDialog(this);

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(jsonPayload);
                var root = doc.RootElement;

                string outputPath = root.TryGetProperty("outputPath", out var pathProp)
                    ? pathProp.GetString() ?? "" : "";
                string filenamePattern = root.TryGetProperty("fileNamePattern", out var nameProp)
                    ? nameProp.GetString() ?? "export" : "export";
                bool overwrite = root.TryGetProperty("overwrite", out var owProp) && owProp.GetBoolean();

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    loadingDialog.Close();
                    await m_mslc_overlay.views.dialogs.MessageDialog.ShowAsync(
                        this, "Thông báo", "Vui lòng chọn thư mục lưu trữ.");
                    return;
                }

                bool enableSub      = root.TryGetProperty("enableSubtitle",      out var subToggle)   && subToggle.GetBoolean();
                bool enableAudio    = root.TryGetProperty("enableAudio",          out var audioToggle) && audioToggle.GetBoolean();
                bool enableVideoSub = root.TryGetProperty("enableVideoSubtitle", out var videoToggle)  && videoToggle.GetBoolean();

                var errors    = new System.Text.StringBuilder();
                var notes     = new System.Text.StringBuilder();
                var successes = new System.Text.StringBuilder();

                if (enableSub || enableVideoSub)
                    await _workspaceVm.FlushPendingAsync();

                // ── Text / Subtitle export ────────────────────────────
                if (enableSub && root.TryGetProperty("subtitleConfig", out var subConfig))
                {
                    try
                    {
                        string format = subConfig.TryGetProperty("format", out var fmtProp)
                            ? fmtProp.GetString() ?? ".SRT" : ".SRT";
                        string contentMode = subConfig.TryGetProperty("contentMode", out var modeProp)
                            ? modeProp.GetString() ?? "Song ngữ (EN + VI)" : "Song ngữ (EN + VI)";
                        string encoding = subConfig.TryGetProperty("encoding", out var encProp)
                            ? encProp.GetString() ?? "UTF-8" : "UTF-8";
                        bool includeStyles = !subConfig.TryGetProperty("includeStyles", out var stylesProp) || stylesProp.GetBoolean();
                        string colorPreset = subConfig.TryGetProperty("colorPreset", out var colorProp)
                            ? colorProp.GetString() ?? "Trắng (White)" : "Trắng (White)";

                        MMslcOverlay.Core.Workspace.Export.IExporter exporter = format.ToUpperInvariant() switch
                        {
                            ".TXT"  => new MMslcOverlay.Core.Workspace.Export.TxtExporter(),
                            ".MD"   => new MMslcOverlay.Core.Workspace.Export.MarkdownExporter(),
                            ".JSON" => new MMslcOverlay.Core.Workspace.Export.JsonExporter(),
                            ".PDF"  => new MMslcOverlay.Core.Workspace.Export.PdfExporter(),
                            ".ASS"  => new MMslcOverlay.Core.Workspace.Export.AssExporter
                            {
                                IncludeStyles = includeStyles,
                                ColorPreset = colorPreset
                            },
                            ".VTT"  => new MMslcOverlay.Core.Workspace.Export.VttExporter(),
                            _       => new MMslcOverlay.Core.Workspace.Export.SrtExporter()
                        };
                        exporter.ContentMode = contentMode;

                        var engine = new MMslcOverlay.Core.Workspace.Export.ExportEngine(_workspaceVm.Service.SegmentRepo);
                        string exportedContent = engine.RunExport(exporter);

                        string ext = format.ToUpperInvariant() switch
                        {
                            ".TXT"  => ".txt",
                            ".MD"   => ".md",
                            ".JSON" => ".json",
                            ".PDF"  => ".pdf",
                            ".ASS"  => ".ass",
                            ".VTT"  => ".vtt",
                            _       => ".srt"
                        };

                        string destFile = System.IO.Path.Combine(outputPath, filenamePattern + ext);

                        if (System.IO.File.Exists(destFile) && !overwrite)
                        {
                            errors.AppendLine($"File đã tồn tại (bỏ qua): {System.IO.Path.GetFileName(destFile)}");
                        }
                        else
                        {
                            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                            {
                                if (System.IO.File.Exists(exportedContent))
                                {
                                    System.IO.File.Copy(exportedContent, destFile, overwrite);
                                    System.IO.File.Delete(exportedContent);
                                }
                            }
                            else
                            {
                                var enc = encoding.Equals("ANSI", StringComparison.OrdinalIgnoreCase)
                                    ? System.Text.Encoding.GetEncoding(1252)
                                    : System.Text.Encoding.UTF8;
                                await System.IO.File.WriteAllTextAsync(destFile, exportedContent, enc);
                            }
                            successes.AppendLine(destFile);
                            services.LoggerService.Log($"[Export] Subtitle exported: {destFile}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.AppendLine($"Lỗi xuất phụ đề: {ex.Message}");
                        services.LoggerService.Log($"[Export] Subtitle export error: {ex.Message}");
                    }
                }

                // ── Audio export ───────────────────────────────────
                if (enableAudio && root.TryGetProperty("audioConfig", out var audioConfig))
                {
                    try
                    {
                        string audioFormat    = audioConfig.TryGetProperty("format",          out var afProp) ? afProp.GetString()  ?? "WAV"       : "WAV";
                        string audioMode      = audioConfig.TryGetProperty("mode",            out var amProp) ? amProp.GetString()  ?? "Merge"     : "Merge";
                        string bitrate        = audioConfig.TryGetProperty("bitrate",         out var brProp) ? brProp.GetString()  ?? "192 kbps"  : "192 kbps";
                        string channels       = audioConfig.TryGetProperty("channels",        out var chProp) ? chProp.GetString()  ?? "Stereo"    : "Stereo";
                        bool normalizeVolume  = !audioConfig.TryGetProperty("normalizeVolume", out var nvProp) || nvProp.GetBoolean();

                        string audioBaseDir = System.IO.Path.Combine(
                            _workspaceVm.Service!.Storage.MslcDir, "audio");

                        // Preferred: session currently in memory
                        string? preferredSessionId = _workspaceVm.Service?.AudioRecorder?.SessionId;

                        // FindBestAudioSessionDir: chon session co audio data, fallback scan all sessions
                        string? sessionDir = FindBestAudioSessionDir(audioBaseDir, preferredSessionId);

                        if (sessionDir == null)
                        {
                            errors.AppendLine("Không có dữ liệu âm thanh: chưa bắt đầu ghi âm trong phiên này.");
                        }
                        else
                        {
                            // Build segment ranges tu DB neu mode = Segment
                            System.Collections.Generic.List<MMslcOverlay.Services.Workspace.SegmentTimeRange>? segRanges = null;
                            if (audioMode.Equals("Segment", StringComparison.OrdinalIgnoreCase))
                            {
                                segRanges = new();
                                string sessionIdName = System.IO.Path.GetFileName(sessionDir);
                                var mergedSegs = _workspaceVm.Service!.SegmentRepo!.GetMergedSegments();
                                foreach (var seg in mergedSegs)
                                {
                                    // Accept segments that belong to this session, OR segments with no session assignment
                                    // (legacy data pre audio-session-tracking). Use AudioOffset when available, fallback to TsMs.
                                    bool belongsToSession = string.IsNullOrEmpty(seg.BaseSegment.AudioSessionId)
                                        || seg.BaseSegment.AudioSessionId == sessionIdName;

                                    if (!belongsToSession) continue;

                                    long startMs = seg.BaseSegment.AudioOffsetMs    ?? seg.BaseSegment.TsStartMs;
                                    long endMs   = seg.BaseSegment.AudioEndOffsetMs ?? seg.BaseSegment.TsEndMs;
                                    string label = seg.BaseSegment.SpeakerId ?? "unknown";

                                    // Skip zero-duration or invalid ranges
                                    if (endMs <= startMs) continue;

                                    segRanges.Add(new MMslcOverlay.Services.Workspace.SegmentTimeRange(label, startMs, endMs));
                                }

                                if (segRanges.Count == 0)
                                {
                                    // No valid segments — fallback to Merge mode
                                    services.LoggerService.Log("[Export] Segment mode: no valid segments found, falling back to Merge mode.");
                                    notes.AppendLine("Chế độ Tách đoạn: không có đoạn hợp lệ, tự động chuyển sang Gộp file.");
                                    segRanges = null;
                                    audioMode = "Merge";
                                }
                            }

                            var progress = new Progress<int>(pct =>
                                services.LoggerService.Log($"[Export] Audio progress: {pct}%"));

                            var req = new MMslcOverlay.Services.Workspace.AudioExportRequest(
                                SessionDir:      sessionDir,
                                OutputPath:      outputPath,
                                FileNamePattern: filenamePattern,
                                Format:          audioFormat,
                                Mode:            audioMode,
                                Channels:        channels,
                                Bitrate:         bitrate,
                                NormalizeVolume: normalizeVolume,
                                Overwrite:       overwrite,
                                SegmentRanges:   segRanges
                            );

                            await MMslcOverlay.Services.Workspace.AudioExportService.ExportAsync(req, progress);
                            successes.AppendLine(outputPath);
                            services.LoggerService.Log($"[Export] Audio exported to: {outputPath}");

                            // FLAC uses WAV container internally (NAudio limitation)
                            if (audioFormat.Equals("FLAC", StringComparison.OrdinalIgnoreCase))
                                notes.AppendLine("FLAC: file đuợc lưu dưới dạng WAV PCM (NAudio không hỗ trợ FLAC encoder). Dùng ffmpeg để chuyển đổi nếu cần.");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.AppendLine($"Lỗi xuất âm thanh: {ex.Message}");
                        services.LoggerService.Log($"[Export] Audio export error: {ex.Message}");
                    }
                }

                if (enableVideoSub && root.TryGetProperty("videoSubtitleConfig", out var videoConfig))
                {
                    string? tempSubPath = null;
                    try
                    {
                        string videoPath = videoConfig.TryGetProperty("videoPath", out var vp)
                            ? vp.GetString() ?? "" : "";
                        string container = videoConfig.TryGetProperty("container", out var ctProp)
                            ? ctProp.GetString() ?? "MKV" : "MKV";
                        string subFmt = videoConfig.TryGetProperty("subtitleFormat", out var sfProp)
                            ? sfProp.GetString() ?? "SRT" : "SRT";
                        string contentMode = videoConfig.TryGetProperty("contentMode", out var cmProp)
                            ? cmProp.GetString() ?? "Chỉ Tiếng Việt (VI)" : "Chỉ Tiếng Việt (VI)";
                        bool setDefault = !videoConfig.TryGetProperty("setAsDefault", out var defProp) || defProp.GetBoolean();
                        long timeOffsetMs = 0;
                        if (videoConfig.TryGetProperty("timeOffsetMs", out var offProp))
                        {
                            if (offProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                                timeOffsetMs = offProp.GetInt64();
                            else if (offProp.ValueKind == System.Text.Json.JsonValueKind.String
                                     && long.TryParse(offProp.GetString(), out var parsedOff))
                                timeOffsetMs = parsedOff;
                        }
                        string colorPreset = videoConfig.TryGetProperty("colorPreset", out var vColorProp)
                            ? vColorProp.GetString() ?? "Trắng (White)" : "Trắng (White)";

                        if (string.IsNullOrWhiteSpace(videoPath) || !System.IO.File.Exists(videoPath))
                        {
                            errors.AppendLine("Không tìm thấy file video để ghép phụ đề.");
                        }
                        else
                        {
                            var ensure = await m_mslc_overlay.views.dialogs.ToolDownloadDialog.EnsureWithUiAsync(this);
                            if (!ensure.Success || string.IsNullOrEmpty(ensure.FfmpegPath))
                            {
                                errors.AppendLine(ensure.ErrorMessage
                                    ?? "Không chuẩn bị được công cụ xử lý video.");
                            }
                            else
                            {
                                bool useAss = container.Equals("MKV", StringComparison.OrdinalIgnoreCase);
                                if (!useAss && subFmt.Contains("ASS", StringComparison.OrdinalIgnoreCase)
                                    && !container.Equals("MP4", StringComparison.OrdinalIgnoreCase))
                                    useAss = true;

                                MMslcOverlay.Core.Workspace.Export.IExporter subExporter = useAss
                                    ? new MMslcOverlay.Core.Workspace.Export.AssExporter
                                    {
                                        IncludeStyles = true,
                                        TimeOffsetMs = timeOffsetMs,
                                        ColorPreset = colorPreset
                                    }
                                    : new MMslcOverlay.Core.Workspace.Export.SrtExporter
                                    {
                                        TimeOffsetMs = timeOffsetMs
                                    };
                                subExporter.ContentMode = contentMode;

                                var engine = new MMslcOverlay.Core.Workspace.Export.ExportEngine(_workspaceVm.Service.SegmentRepo);
                                string subContent = engine.RunExport(subExporter);
                                services.LoggerService.Log(
                                    $"[Export] Video mux offset={timeOffsetMs}ms color={colorPreset} ass={useAss}");

                                string tempExt = useAss ? ".ass" : ".srt";
                                tempSubPath = System.IO.Path.Combine(
                                    System.IO.Path.GetTempPath(),
                                    $"mslc_mux_{System.Guid.NewGuid():N}{tempExt}");
                                await System.IO.File.WriteAllTextAsync(tempSubPath, subContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                                string outExt = container.Equals("MP4", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".mkv";
                                string destVideo = System.IO.Path.Combine(outputPath, filenamePattern + outExt);

                                var muxReq = new MMslcOverlay.Services.Workspace.VideoSubtitleMuxRequest(
                                    VideoPath: videoPath,
                                    SubtitlePath: tempSubPath,
                                    OutputPath: destVideo,
                                    Container: container,
                                    SubtitleCodecHint: useAss ? "ASS" : "SRT",
                                    LanguageCode: MMslcOverlay.Services.Workspace.VideoSubtitleMuxService.LanguageCodeFromContentMode(contentMode),
                                    TrackTitle: MMslcOverlay.Services.Workspace.VideoSubtitleMuxService.TrackTitleFromContentMode(contentMode),
                                    SetAsDefault: setDefault,
                                    Overwrite: overwrite
                                );

                                var muxResult = await MMslcOverlay.Services.Workspace.VideoSubtitleMuxService.MuxAsync(
                                    muxReq, ffmpegPath: ensure.FfmpegPath);
                                if (muxResult.Success)
                                {
                                    successes.AppendLine(muxResult.OutputPath);
                                    services.LoggerService.Log($"[Export] Video+subtitle muxed: {muxResult.OutputPath}");
                                }
                                else
                                {
                                    errors.AppendLine($"Lỗi ghép phụ đề vào video: {muxResult.ErrorMessage}");
                                    services.LoggerService.Log($"[Export] Video mux error: {muxResult.ErrorMessage}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.AppendLine($"Lỗi ghép phụ đề vào video: {ex.Message}");
                        services.LoggerService.Log($"[Export] Video subtitle mux error: {ex.Message}");
                    }
                    finally
                    {
                        if (tempSubPath != null)
                        {
                            try { System.IO.File.Delete(tempSubPath); } catch { /* ignore */ }
                        }
                    }
                }

                // ── Post-export ────────────────────────────────────────
                _workspaceVm.RefreshSessionFiles();

                loadingDialog.Close();

                if (errors.Length > 0)
                {
                    string detail = errors.ToString().Trim();
                    if (successes.Length > 0)
                        detail = "Một phần thành công:\n" + successes.ToString().Trim() + "\n\nCảnh báo:\n" + detail;
                    if (notes.Length > 0) detail += "\n\n[Ghi chú] " + notes.ToString().Trim();
                    await m_mslc_overlay.views.dialogs.MessageDialog.ShowAsync(
                        this, "Xuất file hoàn tất (có cảnh báo)", detail);
                }
                else
                {
                    string successMsg = successes.Length > 0
                        ? successes.ToString().Trim()
                        : outputPath;
                    if (notes.Length > 0) successMsg += "\n\n[Ghi chú] " + notes.ToString().Trim();
                    await m_mslc_overlay.views.dialogs.MessageDialog.ShowAsync(
                        this, "Xuất file thành công",
                        $"Dữ liệu đã được lưu:\n{successMsg}");
                }
            }
            catch (Exception ex)
            {
                loadingDialog.Close();
                services.LoggerService.Log($"[Export] ProcessAdvancedExportPayloadAsync failed: {ex.Message}");
                await m_mslc_overlay.views.dialogs.MessageDialog.ShowAsync(
                    this, "Lỗi xuất file", $"Đã xảy ra lỗi không mong đợi:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Scan audioBaseDir for the session with actual audio data (at least 1 chunk with SizeBytes > 0).
        /// Prefer preferredSessionId if it already has data; otherwise pick the session with the
        /// largest TotalDurationMs as fallback (most complete recording in the workspace).
        /// Returns null if no session with audio data exists.
        /// </summary>
        private static string? FindBestAudioSessionDir(string audioBaseDir, string? preferredSessionId)
        {
            if (!System.IO.Directory.Exists(audioBaseDir))
                return null;

            // Helper: check if a sessionDir has at least one PCM chunk with data
            static bool HasAudioData(string sessionDir)
            {
                string metaPath = System.IO.Path.Combine(sessionDir, "metadata.json");
                if (!System.IO.File.Exists(metaPath)) return false;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(metaPath));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("Chunks", out var chunksEl) &&
                        !root.TryGetProperty("chunks", out chunksEl)) return false;
                    foreach (var chunk in chunksEl.EnumerateArray())
                    {
                        long size = 0;
                        if (chunk.TryGetProperty("SizeBytes", out var sbEl) ||
                            chunk.TryGetProperty("sizeBytes", out sbEl))
                            size = sbEl.GetInt64();
                        if (size > 0) return true;

                        // Also check actual file on disk
                        string? fileName = null;
                        if (chunk.TryGetProperty("FileName", out var fnEl) ||
                            chunk.TryGetProperty("fileName", out fnEl))
                            fileName = fnEl.GetString();
                        if (fileName != null)
                        {
                            string chunkPath = System.IO.Path.Combine(sessionDir, fileName);
                            if (System.IO.File.Exists(chunkPath) && new System.IO.FileInfo(chunkPath).Length > 0)
                                return true;
                        }
                    }
                }
                catch { /* malformed metadata — skip */ }
                return false;
            }

            // 1. Try preferred session first
            if (!string.IsNullOrEmpty(preferredSessionId))
            {
                string preferred = System.IO.Path.Combine(audioBaseDir, preferredSessionId);
                if (HasAudioData(preferred)) return preferred;
            }

            // 2. Scan all subdirs and pick the one with the most audio data
            string? bestDir = null;
            long bestDuration = 0;

            foreach (string dir in System.IO.Directory.GetDirectories(audioBaseDir))
            {
                if (!HasAudioData(dir)) continue;

                // Read TotalDurationMs for ranking
                long duration = 0;
                string metaPath = System.IO.Path.Combine(dir, "metadata.json");
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(metaPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("TotalDurationMs", out var d) ||
                        root.TryGetProperty("totalDurationMs", out d))
                        duration = d.GetInt64();
                }
                catch { }

                if (duration >= bestDuration)
                {
                    bestDuration = duration;
                    bestDir = dir;
                }
            }

            return bestDir;
        }

        private async System.Threading.Tasks.Task NewWorkspaceFlowAsync()
        {
            // Dirty-check trước khi đóng workspace hiện tại
            if (_workspaceVm.IsOpen && _workspaceVm.IsDirty)
            {
                var choice = await ShowUnsavedChangesDialogAsync();
                if (choice == UnsavedChoice.Cancel) return;
                if (choice == UnsavedChoice.Save) await _workspaceVm.FlushPendingAsync();
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            // Custom dialog: tên + path preview + nút "Đổi..." để chọn parent
            // Tránh yêu cầu user tạo folder trong Windows Explorer (bad UX)
            string defaultParent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MMslcOverlay", "workspaces");

            string? newPath = await ShowNewWorkspaceDialogAsync(topLevel, defaultParent);
            if (newPath == null) return;

            try
            {
                _workspaceVm.OpenOrCreate(newPath);
            }
            catch (Exception ex)
            {
                await ShowErrorMessageAsync("Lỗi tạo workspace", ex.Message);
            }
        }

        /// <summary>
        /// Dialog tạo workspace mới: nhập tên + xem preview path + chọn thư mục lưu.
        /// Trả về full path đã được validate, hoặc null nếu user cancel.
        /// </summary>
        private async System.Threading.Tasks.Task<string?> ShowNewWorkspaceDialogAsync(
            TopLevel topLevel, string defaultParent)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
            string parentDir = defaultParent;

            // — Controls —
            var nameBox = new TextBox
            {
                Watermark = "Tên phiên làm việc",
                Margin = new Avalonia.Thickness(0, 0, 0, 4)
            };

            var pathPreview = new TextBlock
            {
                FontSize = 11,
                Foreground = this.FindResource("TextSecondaryBrush") as IBrush ?? Brushes.Gray,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Text = Path.Combine(defaultParent, "…")
            };

            var errorLabel = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.OrangeRed,
                IsVisible = false
            };

            var changeBtn = new Button
            {
                Content = "Đổi thư mục…",
                Padding = new Avalonia.Thickness(10, 4),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            var createBtn = new Button
            {
                Content = "Tạo workspace",
                IsDefault = true,
                Padding = new Avalonia.Thickness(16, 6),
                IsEnabled = false
            };

            var cancelBtn = new Button
            {
                Content = "Hủy",
                IsCancel = true,
                Padding = new Avalonia.Thickness(16, 6)
            };

            // — Live path preview khi user gõ tên —
            string GetCurrentPath()
            {
                var raw = nameBox.Text?.Trim() ?? string.Empty;
                foreach (var c in Path.GetInvalidFileNameChars())
                    raw = raw.Replace(c, '_');
                return string.IsNullOrEmpty(raw)
                    ? Path.Combine(parentDir, "…")
                    : Path.Combine(parentDir, raw);
            }

            void RefreshPreview()
            {
                var p = GetCurrentPath();
                pathPreview.Text = $"Sẽ tạo: {p}";

                var name = nameBox.Text?.Trim() ?? string.Empty;
                createBtn.IsEnabled = !string.IsNullOrEmpty(name);

                // Cảnh báo nếu đã tồn tại
                if (!string.IsNullOrEmpty(name) && Directory.Exists(p))
                {
                    var ws = new MMslcOverlay.Core.Workspace.Storage.WorkspaceStorage(p);
                    if (ws.IsValidWorkspace())
                    {
                        errorLabel.Text = "Workspace này đã tồn tại. Dùng File > Open Workspace để mở.";
                        errorLabel.IsVisible = true;
                        createBtn.IsEnabled = false;
                    }
                    else
                    {
                        errorLabel.Text = "Thư mục đã tồn tại (không phải workspace) — sẽ dùng làm workspace.";
                        errorLabel.Foreground = Brushes.Orange;
                        errorLabel.IsVisible = true;
                        // Cho phép tạo nếu folder rỗng
                        createBtn.IsEnabled = !Directory.EnumerateFileSystemEntries(p).Any();
                    }
                }
                else
                {
                    errorLabel.IsVisible = false;
                    errorLabel.Foreground = Brushes.OrangeRed;
                }
            }

            nameBox.TextChanged += (_, _) => RefreshPreview();

            // — "Đổi thư mục…" mở FolderPicker chọn parent —
            changeBtn.Click += async (_, _) =>
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
                    new Avalonia.Platform.Storage.FolderPickerOpenOptions
                    {
                        Title = "Chọn thư mục lưu workspace",
                        AllowMultiple = false
                    });
                if (folders.Count > 0)
                {
                    parentDir = folders[0].Path.LocalPath;
                    RefreshPreview();
                }
            };

            // — Layout —
            var dialog = new Window
            {
                Title = "Phiên làm việc mới",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new Border
                {
                    Padding = new Avalonia.Thickness(24),
                    Child = new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Tên phiên làm việc:", Foreground = Brushes.White, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                            nameBox,
                            new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                Spacing = 8,
                                Children =
                                {
                                    pathPreview,
                                    changeBtn
                                }
                            },
                            errorLabel,
                            new Separator { Margin = new Avalonia.Thickness(0, 4) },
                            new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                Spacing = 8,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                Children = { createBtn, cancelBtn }
                            }
                        }
                    }
                }
            };

            createBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(GetCurrentPath());
                dialog.Close();
            };
            cancelBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(null);
                dialog.Close();
            };
            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(this);
            return await tcs.Task;
        }



        private async System.Threading.Tasks.Task<string?> ShowInputDialogAsync(string title, string prompt)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
            var input = new TextBox { Watermark = "Session name" };
            var dialog = new Window
            {
                Title = title,
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new Border
                {
                    Padding = new Avalonia.Thickness(24),
                    Child = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = prompt, Foreground = Brushes.White, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            input,
                            new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                Spacing = 8,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                Children =
                                {
                                    new Button { Content = "OK", Width = 80, IsDefault = true },
                                    new Button { Content = "Hủy", Width = 80, IsCancel = true }
                                }
                            }
                        }
                    }
                }
            };

            var btnPanel = (StackPanel)((StackPanel)((Border)dialog.Content!).Child!).Children[2];
            var okBtn = (Button)btnPanel.Children[0];
            var cancelBtn = (Button)btnPanel.Children[1];

            okBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(string.IsNullOrWhiteSpace(input.Text)
                    ? $"session_{DateTime.Now:yyyyMMdd_HHmmss}"
                    : input.Text.Trim());
                dialog.Close();
            };
            cancelBtn.Click += (_, _) =>
            {
                tcs.TrySetResult(null);
                dialog.Close();
            };

            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private async void OnOpenWorkspaceMenuClick(object? sender, RoutedEventArgs e)
        {
            // Dirty-check trước khi switch workspace
            if (_workspaceVm.IsOpen && _workspaceVm.IsDirty)
            {
                var choice = await ShowUnsavedChangesDialogAsync();
                if (choice == UnsavedChoice.Cancel) return;
                if (choice == UnsavedChoice.Save) await _workspaceVm.FlushPendingAsync();
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Chọn thư mục workspace",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;

            string selectedPath = folders[0].Path.LocalPath;

            var storage = new MMslcOverlay.Core.Workspace.Storage.WorkspaceStorage(selectedPath);
            if (!storage.IsValidWorkspace())
            {
                await ShowErrorMessageAsync("Không phải workspace hợp lệ",
                    "Thư mục đã chọn không chứa workspace MMslcOverlay hợp lệ.\n" +
                    "Dùng File > New Workspace để tạo workspace mới.");
                return;
            }

            // Mở trong cùng window (không tạo window mới) → tránh leak VM
            try
            {
                _workspaceVm.OpenOrCreate(selectedPath);
            }
            catch (Exception ex)
            {
                await ShowErrorMessageAsync("Lỗi mở workspace", ex.Message);
            }
        }

        private async System.Threading.Tasks.Task ShowErrorMessageAsync(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(24),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock 
                        { 
                            Text = message, 
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Foreground = Brushes.White
                        },
                        new Button 
                        { 
                            Content = "OK", 
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right 
                        }
                    }
                }
            };
            var btn = ((StackPanel)dialog.Content!).Children[1] as Button;
            if (btn != null) btn.Click += (s, e) => dialog.Close();
            await dialog.ShowDialog(this);
        }

        // Gap 7: Close Workspace menu handler — với dirty check
        private async void OnCloseWorkspaceMenuClick(object? sender, RoutedEventArgs e)
        {
            if (_workspaceVm.IsDirty)
            {
                var choice = await ShowUnsavedChangesDialogAsync();
                if (choice == UnsavedChoice.Cancel) return;
                if (choice == UnsavedChoice.Save)
                    await _workspaceVm.FlushPendingAsync();
            }

            if (_workspaceVm.IsRecording) _workspaceVm.StopRecording();
            _workspaceVm.CloseWorkspace();
            
            // Update session control button to "Start Session" state
            var btn = this.FindControl<Button>("SessionControlBtn");
            var icon = this.FindControl<MaterialIcon>("SessionControlIcon");
            var label = this.FindControl<TextBlock>("SessionControlLabel");
            if (btn != null && icon != null && label != null)
            {
                UpdateButtonLabel(btn, icon, label);
            }
        }

        /// <summary>
        /// Flush pending freeform edits only (không đóng workspace).
        /// Dùng cho application Closing.
        /// </summary>
        private async System.Threading.Tasks.Task FlushPendingEditsAsync()
        {
            await _workspaceVm.FlushPendingAsync();
        }

        /// <summary>
        /// Dọn dẹp các service dùng chung trước khi tắt window.
        /// </summary>
        private void CleanupServices()
        {
            _shortSentenceBuffer.Flush();   // ATOM50
            _shortSentenceBuffer.Dispose();
            _aiService.Dispose();           // ATOM81
            _revisionWindow.Dispose();      // ATOM80
            _hiderService.Dispose();
            _pipeService.Dispose();
            _resourceTimer.Stop();
            _uiUpdateTimer.Stop();
            _hotkeyManager?.Dispose();
            _focusKeyController?.Dispose();
            OfflineTranslationServerManager.StopServer();
            _diarizerManager?.Dispose();
        }

        /// <summary>
        /// Hiển thị SaveFileDialog cho export. Trả về path, hoặc null nếu cancel.
        /// </summary>
        private async System.Threading.Tasks.Task<string?> ShowSaveFileDialogAsync(string label, string defaultExt, string suggestedName)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return null;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = $"Export {label}",
                SuggestedFileName = suggestedName,
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(label)
                    {
                        Patterns = new[] { $"*.{defaultExt}" }
                    }
                },
                DefaultExtension = defaultExt
            });

            return file?.Path.LocalPath;
        }

        /// <summary>Mở file bằng app mặc định của OS (Explore note/srt/pdf).</summary>
        private void OpenFileInDefaultApp(string fullPath)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [ERROR] Cannot open file: {ex.Message}\n");
            }
        }

        /// <summary>Copy workspace path vào clipboard.</summary>
        private async void CopyPathBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_workspaceVm.WorkspacePath) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_workspaceVm.WorkspacePath);
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Copied workspace path to clipboard.\n");
            }
        }

        /// <summary>Sidebar session file item clicked → mở bằng default app.</summary>
        private void SessionFileItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is MMslcOverlay.ViewModels.Workspace.WorkspaceFileItem item)
            {
                _workspaceVm.OpenSessionFile(item);
            }
        }

        /// <summary>Import Script (txt/md) vào workspace hiện tại.</summary>
        private async void OnImportScriptRequested()
        {
            if (!_workspaceVm.IsOpen)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [WARNING] No workspace open. Open a workspace first.\n");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import Script",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Script files") { Patterns = new[] { "*.txt", "*.md" } }
                }
            });

            if (files.Count == 0) return;
            await _workspaceVm.ImportScriptAsync(files[0].Path.LocalPath);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Imported script: {Path.GetFileName(files[0].Path.LocalPath)}\n");
        }

        /// <summary>
        /// Cập nhật session name, path, last modified trên sidebar.
        /// Dùng FindControl trực tiếp vì compiled bindings bị conflict khi Window x:DataType != set.
        /// </summary>
        private void UpdateSessionDisplay()
        {
            var nameText = this.FindControl<TextBlock>("SessionNameText");
            var pathText = this.FindControl<TextBlock>("WorkspacePathText");
            var lastModText = this.FindControl<TextBlock>("LastModifiedText");

            if (nameText != null) nameText.Text = _workspaceVm.DisplayName;
            if (pathText != null) pathText.Text = _workspaceVm.WorkspacePath;
            if (lastModText != null) lastModText.Text = _workspaceVm.LastModifiedDisplay;

            RefreshSessionFilesList();
        }

        /// <summary>
        /// Cập nhật danh sách file sidebar.
        /// </summary>
        private void RefreshSessionFilesList()
        {
            var listPanel = this.FindControl<StackPanel>("SessionFilesPanel");
            if (listPanel == null) return;

            listPanel.Children.Clear();

            if (!_workspaceVm.IsOpen || _workspaceVm.SessionFiles.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = "Session files appear after recording or exporting.",
                    FontSize = 11,
                    Foreground = this.FindResource("TextTertiaryBrush") as IBrush,
                    FontStyle = Avalonia.Media.FontStyle.Italic
                });
                return;
            }

            foreach (var item in _workspaceVm.SessionFiles)
            {
                var iconKind = item.Icon;
                var kindEnum = Enum.TryParse<Material.Icons.MaterialIconKind>(iconKind, out var k) ? k : Material.Icons.MaterialIconKind.FileDocumentOutline;

                var icon = new Material.Icons.Avalonia.MaterialIcon
                {
                    Kind = kindEnum,
                    Width = 14,
                    Height = 14,
                    Foreground = this.FindResource("TextSecondaryBrush") as IBrush
                };

                var text = new TextBlock
                {
                    Text = item.FileName,
                    FontSize = 12,
                    Foreground = this.FindResource("TextPrimaryBrush") as IBrush,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                grid.Children.Add(icon);
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);

                var border = new Border
                {
                    Classes = { "SidebarItem" },
                    Child = grid,
                    Tag = item
                };

                border.PointerPressed += (s, e) =>
                {
                    if (border.Tag is MMslcOverlay.ViewModels.Workspace.WorkspaceFileItem fileItem)
                        _workspaceVm.OpenSessionFile(fileItem);
                };

                listPanel.Children.Add(border);
            }
        }

        private void AppendLog(string logLine)
        {
            lock(_logLock) {
                _rawLogs.Add(logLine);
                if (_rawLogs.Count > 100) _rawLogs.RemoveAt(0);
                _isLogDirty = true;
            }
            LoggerService.Log(logLine);
            Console.Write(logLine);
        }

        private void OnUiUpdateTimerTick(object? sender, EventArgs e)
        {
            if (_isLogDirty)
            {
                lock(_logLock) {
                    // RawTextLog has been blanked from UI. Logs are saved via LoggerService.
                    _isLogDirty = false;
                }
            }

            if (_isPartialCaptionDirty)
            {
                if (_currentOverlay != null && _currentOverlay.IsVisible && !IsTranslationEnabled)
                {
                    _currentOverlay.SetStreamingText(_lastPartialCaption);
                }
                _isPartialCaptionDirty = false;
            }

            if (_isTranslationDirty && IsTranslationEnabled)
            {
                string displayTxt;
                lock(_translationLock) {
                    displayTxt = _translationDisplayBuffer;
                }
                if (_currentOverlay != null && _currentOverlay.IsVisible)
                {
                    _currentOverlay.SetStreamingText(displayTxt);
                }
                _isTranslationDirty = false;
            }
        }

        private void OnResourceTimerTick(object? sender, EventArgs e)
        {
            var metrics = _sysMonitor.GetMetrics();
            ResourceUsageText.Text = $"SYS: {metrics.sysCpu:F1}% CPU {metrics.sysRamMb:F0}MB | APP: {metrics.appCpu:F1}% CPU {metrics.appRamMb:F0}MB";
            
            _ = UpdateStatusVisualsAsync();
        }

        private bool? _cachedHasCuda = null;

        private async System.Threading.Tasks.Task UpdateStatusVisualsAsync()
        {
            var gray = SolidColorBrush.Parse("#CBCCC9");
            var yellow = SolidColorBrush.Parse("#FFAA00");
            var red = SolidColorBrush.Parse("#FF3333");
            var green = SolidColorBrush.Parse("#00FF88");

            // 1. Python runtime
            string serverDir = OfflineTranslationServerManager.FindServerDirectory();
            StatusDotPython.Fill = string.IsNullOrEmpty(serverDir) ? gray : green;

            // 2. Live caption
            StatusDotCaption.Fill = _currentHookState switch
            {
                HookState.Waiting => gray,
                HookState.Detected => yellow,
                HookState.Injected => green,
                HookState.Failed => red,
                _ => gray
            };

            // 3. CUDA & 5. Local Network
            if (OfflineTranslationServerManager.State == OfflineServerState.Ready)
            {
                StatusDotNetwork.Fill = green;
                
                if (_cachedHasCuda == null)
                {
                    try
                    {
                        using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                        var response = await httpClient.GetAsync($"http://127.0.0.1:{OfflineTranslationServerManager.ServerPort}/status");
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync();
                            using var doc = System.Text.Json.JsonDocument.Parse(content);
                            if (doc.RootElement.TryGetProperty("has_cuda", out var prop))
                            {
                                _cachedHasCuda = prop.GetBoolean();
                            }
                        }
                    }
                    catch {}
                }
                
                StatusDotCuda.Fill = _cachedHasCuda == true ? green : (_cachedHasCuda == false ? yellow : gray);
            }
            else
            {
                StatusDotNetwork.Fill = OfflineTranslationServerManager.State == OfflineServerState.Starting ? yellow : gray;
                StatusDotCuda.Fill = gray;
                _cachedHasCuda = null; // reset if server goes down
            }

            // 4. Extractor module
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string extractorDir = AppPathHelper.GetExtractorDirectory();
            bool hasHost = File.Exists(Path.Combine(extractorDir, "Host.exe")) || File.Exists(Path.Combine(baseDir, "extractor", "Host.exe")) || File.Exists(Path.Combine(baseDir, "Host.exe"));
            bool hasAgent = File.Exists(Path.Combine(extractorDir, "Agent.dll")) || File.Exists(Path.Combine(baseDir, "extractor", "Agent.dll")) || File.Exists(Path.Combine(baseDir, "Agent.dll"));
            
            StatusDotExtractor.Fill = (hasHost && hasAgent) ? green : red;
        }

        private void DetectTargetProcess()
        {
            bool isRunning = LiveCaptionUtils.IsLiveCaptionRunning();
            if (isRunning)
            {
                _currentHookState = HookState.Detected;
                _hiderService.PreFindTargetProcessId("LiveCaptions");
            }
            else
            {
                _currentHookState = HookState.Waiting;
            }
            UpdateDynamicStrings();
        }

        private void UpdateDynamicStrings()
        {
            uint pid = LiveCaptionUtils.GetLiveCaptionProcessId();

            if (pid != 0)
            {
                TargetPidText.Text = $"{LanguageManager.GetString("Status_PidPrefix")}{pid}";
            }
            else
            {
                TargetPidText.Text = LanguageManager.GetString("Status_PidNotRunning");
            }

            switch (_currentHookState)
            {
                case HookState.Waiting:
                    HookStatusDot.Fill = SolidColorBrush.Parse("#FF3333");
                    HookStatusText.Text = LanguageManager.GetString("Status_Waiting");
                    break;
                case HookState.Detected:
                    HookStatusDot.Fill = SolidColorBrush.Parse("#FFAA00");
                    HookStatusText.Text = LanguageManager.GetString("Status_Detected");
                    break;
                case HookState.Injected:
                    HookStatusDot.Fill = SolidColorBrush.Parse("#00FF88");
                    HookStatusText.Text = LanguageManager.GetString("Status_Injected");
                    break;
                case HookState.Failed:
                    HookStatusDot.Fill = SolidColorBrush.Parse("#FF3333");
                    HookStatusText.Text = LanguageManager.GetString("Status_Failed");
                    break;
            }

            // Cập nhật thông tin AI Model & Topic
            string topic = string.IsNullOrWhiteSpace(ContextTopic) ? "None" : ContextTopic;
            StatusBarInfoText.Text = string.Format(LanguageManager.GetString("Status_InfoFormat"), "Gemini 1.5 Pro", topic);

            // Cập nhật trạng thái vận hành chính
            if (_pipeService.IsRunning)
            {
                StatusBarMainText.Text = LanguageManager.GetString("Status_PipeMonitoring");
            }
            else
            {
                StatusBarMainText.Text = LanguageManager.GetString("Status_Ready");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // UNIFIED SESSION CONTROL BUTTON
        // ═══════════════════════════════════════════════════════════════════
        
        private enum SessionState
        {
            Idle,       // Not started
            Starting,   // Opening Live Caption + Injecting
            Recording,  // Active recording session
            Stopping    // Stopping recording
        }
        
        private SessionState _sessionState = SessionState.Idle;
        
        /// <summary>
        /// Unified button that controls entire pipeline:
        /// IDLE → Opens Live Caption + Injects + Creates Workspace + Starts Recording
        /// RECORDING → Stops Recording + Closes Workspace
        /// </summary>
        private async void SessionControlBtn_Click(object sender, RoutedEventArgs e)
        {
            var btn = this.FindControl<Button>("SessionControlBtn");
            var icon = this.FindControl<MaterialIcon>("SessionControlIcon");
            var label = this.FindControl<TextBlock>("SessionControlLabel");
            
            if (btn == null || icon == null || label == null) return;
            
            switch (_sessionState)
            {
                case SessionState.Idle:
                    await StartUnifiedSessionAsync(btn, icon, label);
                    break;
                    
                case SessionState.Recording:
                    await StopUnifiedSessionAsync(btn, icon, label);
                    break;
                    
                default:
                    // Button disabled during Starting/Stopping
                    break;
            }
        }
        
        private async System.Threading.Tasks.Task StartUnifiedSessionAsync(Button btn, MaterialIcon icon, TextBlock label)
        {
            _sessionState = SessionState.Starting;
            btn.IsEnabled = false;
            label.Text = "Starting...";
            
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [SESSION] Starting unified session...\n");
                
                // Step 1: Check Live Caption
                DetectTargetProcess();
                uint pid = _hiderService.TargetProcessId;
                
                if (pid == 0)
                {
                    // Live Caption not running - open settings page to let user enable it
                    AppendLog($"[{timestamp}] [SESSION] Live Caption not detected, opening Settings...\n");
                    bool opened = LiveCaptionUtils.LaunchLiveCaptionSettings();
                    
                    if (!opened)
                    {
                        AppendLog($"[{timestamp}] [SESSION ERROR] Failed to open Live Caption settings.\n");
                        _sessionState = SessionState.Idle;
                        btn.IsEnabled = true;
                        UpdateButtonLabel(btn, icon, label);
                        return;
                    }
                    
                    // Show instruction to user
                    AppendLog($"[{timestamp}] [SESSION] Please enable Live Captions in Settings, then click Start Session again.\n");
                    _sessionState = SessionState.Idle;
                    btn.IsEnabled = true;
                    UpdateButtonLabel(btn, icon, label);
                    return;
                }
                
                AppendLog($"[{timestamp}] [SESSION] Live Caption detected (PID {pid})\n");
                
                // Step 2: Inject DLL
                AppendLog($"[{timestamp}] [SESSION] Injecting hook DLL...\n");
                bool injected = await _injectorService.InjectAsync(pid);
                
                if (!injected)
                {
                    AppendLog($"[{timestamp}] [SESSION ERROR] Injection failed. Check UAC permissions.\n");
                    _sessionState = SessionState.Idle;
                    btn.IsEnabled = true;
                    UpdateButtonLabel(btn, icon, label);
                    return;
                }
                
                HookStatusDot.Fill = SolidColorBrush.Parse("#00FF88");
                HookStatusText.Text = "Injected";
                AppendLog($"[{timestamp}] [SESSION] Hook injected successfully\n");
                
                // Step 3: Start pipe server
                _pipeService.Start();
                AppendLog($"[{timestamp}] [SESSION] Named Pipe server started\n");
                
                // Step 4: Open/Create workspace (ONLY if not already open)
                if (!_workspaceVm.IsOpen)
                {
                    string workspaceName = $"Session_{DateTime.Now:yyyyMMdd_HHmmss}";
                    string workspacePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        workspaceName
                    );
                    
                    _workspaceVm.OpenOrCreate(workspacePath);
                    AppendLog($"[{timestamp}] [SESSION] Workspace created: {workspacePath}\n");
                }
                else
                {
                    AppendLog($"[{timestamp}] [SESSION] Using existing workspace: {_workspaceVm.WorkspacePath}\n");
                }
                
                // Step 5: Start recording
                _workspaceVm.StartRecording();
                AppendLog($"[{timestamp}] [SESSION] Audio recording started\n");
                
                // Step 5: Restart pipe if it was stopped during a previous pause
                // (StartRecording without pipe = silent session)
                if (!_pipeService.IsRunning)
                {
                    _pipeService.Start();
                    AppendLog($"[{timestamp}] [SESSION] LiveCaption pipe restarted\n");
                }

                // Step 6: Start/Resume Speaker Diarizer (session-scoped, not overlay-scoped)
                if (ConfigManager.Current.EnableDiarizer)
                {
                    if (_diarizerManager != null)
                    {
                        // Diarizer already running (was soft-paused or pre-warmed) → resume audio stream only
                        await _diarizerManager.ResumeAudioAsync();
                        AppendLog($"[{timestamp}] [SESSION] Diarizer audio stream resumed\n");
                    }
                    else
                    {
                        // Edge case: pre-warm failed or was skipped → fresh init
                        await InitializeDiarizerAsync();
                        WireDiarizerCallbacks();
                    }
                }
                
                // Update button to "Pause Recording"
                _sessionState = SessionState.Recording;
                btn.IsEnabled = true;
                icon.Kind = Material.Icons.MaterialIconKind.Pause;
                label.Text = "Pause";
                btn.Classes.Remove("PrimaryBtn");
                btn.Classes.Add("DangerBtn");
                ToolTip.SetTip(btn, "Pause recording (stop accepting new segments)");
                
                AppendLog($"[{timestamp}] [SESSION] Session started successfully. Speak to begin transcription.\n");
            }
            catch (Exception ex)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [SESSION ERROR] {ex.Message}\n");
                _sessionState = SessionState.Idle;
                btn.IsEnabled = true;
                UpdateButtonLabel(btn, icon, label);
            }
        }
        
        private async System.Threading.Tasks.Task StopUnifiedSessionAsync(Button btn, MaterialIcon icon, TextBlock label)
        {
            _sessionState = SessionState.Stopping;
            btn.IsEnabled = false;
            label.Text = "Pausing...";
            
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [SESSION] Pausing recording...\n");
                
                // Step 1: Stop pipe FIRST — prevent LiveCaption from pushing more text
                // Bug 3: Without stopping the pipe, LC keeps appending machine text even
                // while "paused", causing repeat text and diarizer audio noise.
                _pipeService.Stop();
                AppendLog($"[{timestamp}] [SESSION] LiveCaption pipe stopped\n");
                
                // Step 2: Stop recording workspace
                if (_workspaceVm.IsRecording)
                {
                    _workspaceVm.StopRecording();
                    AppendLog($"[{timestamp}] [SESSION] Audio recording stopped\n");
                }
                
                // Step 2: Soft-pause diarizer — stop audio stream, keep process alive
                // Engine stays warm so resume is instant (no model reload needed)
                if (_diarizerManager != null)
                {
                    await _diarizerManager.SendCommandAsync(new { cmd = "pause_audio" });
                    AppendLog($"[{timestamp}] [SESSION] Diarizer audio stream paused (process still alive)\n");
                }
                
                // Step 3: Flush pending edits
                if (_workspaceVm.IsDirty)
                {
                    await FlushPendingEditsAsync();
                    AppendLog($"[{timestamp}] [SESSION] Pending edits flushed\n");
                }
                
                // Step 4: Workspace stays OPEN (user can continue editing, export, etc.)
                // User must explicitly close via File menu or Close Workspace button
                
                // Update button state
                _sessionState = SessionState.Idle;
                btn.IsEnabled = true;
                UpdateButtonLabel(btn, icon, label);
                
                AppendLog($"[{timestamp}] [SESSION] Recording paused. Workspace remains open for editing/export.\n");
            }
            catch (Exception ex)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [SESSION ERROR] {ex.Message}\n");
                _sessionState = SessionState.Idle;
                btn.IsEnabled = true;
                UpdateButtonLabel(btn, icon, label);
            }
        }
        
        /// <summary>
        /// Update button label/icon based on workspace state (context-aware)
        /// </summary>
        private void UpdateButtonLabel(Button btn, MaterialIcon icon, TextBlock label)
        {
            if (_workspaceVm.IsOpen)
            {
                // Workspace is open - show "Resume" button
                icon.Kind = Material.Icons.MaterialIconKind.PlayCircle;
                label.Text = "Resume";
                btn.Classes.Remove("DangerBtn");
                btn.Classes.Add("PrimaryBtn");
                ToolTip.SetTip(btn, "Resume recording (continue accepting segments into current workspace)");
            }
            else
            {
                // No workspace open - show "Start Session" button
                icon.Kind = Material.Icons.MaterialIconKind.PlayCircle;
                label.Text = "Start Session";
                btn.Classes.Remove("DangerBtn");
                btn.Classes.Add("PrimaryBtn");
                ToolTip.SetTip(btn, "Start new transcription session (opens Live Caption, injects, starts recording)");
            }
        }
        
        // ═══════════════════════════════════════════════════════════════════
        // LEGACY MANUAL INJECTION (kept for debugging)
        // ═══════════════════════════════════════════════════════════════════
        
        private async void InjectBtn_Click(object sender, RoutedEventArgs e)
        {
            DetectTargetProcess();

            uint pid = _hiderService.TargetProcessId;
            if (pid == 0)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [WARNING] LiveCaptions chưa chạy! Vui lòng khởi động Windows Live Captions trước.\n");
                return;
            }

            string ts = DateTime.Now.ToString("HH:mm:ss");
            AppendLog($"[{ts}] [SYSTEM] Starting DLL injection into PID {pid}...\n");

            bool success = await _injectorService.InjectAsync(pid);

            if (success)
            {
                HookStatusDot.Fill = SolidColorBrush.Parse("#00FF88");
                HookStatusText.Text = "Injected";
                
                string nowTs = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{nowTs}] [SYSTEM] DLL injected successfully. Starting Named Pipe listener...\n");
                
                _pipeService.Start();

                // Nâng cao Fault Tolerance: Ẩn lại tiến trình Live Captions mới nếu overlay đang mở
                if (_currentOverlay != null && _currentOverlay.IsVisible)
                {
                    _currentOverlay.ReHideTargetApp();
                }
            }
            else
            {
                HookStatusDot.Fill = SolidColorBrush.Parse("#FF3333");
                HookStatusText.Text = "Failed";
                
                string nowTs = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{nowTs}] [ERROR] Injection failed or Administrator permission was denied.\n");
            }
        }

        private async System.Threading.Tasks.Task InitializeDiarizerAsync()
        {
            if (_diarizerManager != null) return;

            // Check feature flag first
            if (!ConfigManager.Current.EnableDiarizer)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Disabled in config. Enable in Preferences.\n");
                _workspaceVm.NavPane?.SetDiarizerUnavailable("Feature disabled in Preferences. Enable to use speaker diarization.");
                return;
            }
            
            _diarizerManager = new DiarizerProcessManager();
            
            _diarizerManager.OnLog += (logMessage) => 
            {
                AppendLog($"[DIARIZER] {logMessage}\n");
            };

            _diarizerManager.OnEvent += HandleDiarizerEvent;

            // Use PluginManifestService for production-safe path resolution
            var manifest = await PluginManifestService.LoadManifestAsync();
            var atom32Entry = manifest?.Atoms.FirstOrDefault(a => a.Id == "atom32");

            if (atom32Entry == null)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER ERROR] atom32 not found in plugins.manifest.json\n");
                _diarizerManager.Dispose();
                _diarizerManager = null;
                _workspaceVm.NavPane?.SetDiarizerUnavailable("Plugin not found in manifest. Check plugins.manifest.json.");
                return;
            }

            string installDir = PluginManifestService.ResolveInstallDir(atom32Entry.InstallDir);
            string pythonExe = Path.Combine(installDir, ".venv", "Scripts", "python.exe");
            string scriptPath = Path.Combine(installDir, atom32Entry.EntryScript);

            if (!File.Exists(pythonExe) || !File.Exists(scriptPath))
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER ERROR] Cannot find python ({pythonExe}) or script ({scriptPath}).\n");
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Install atom32 from Preferences > Utilities > Speaker Diarization.\n");
                _diarizerManager.Dispose();
                _diarizerManager = null;
                _workspaceVm.NavPane?.SetDiarizerUnavailable("Plugin not installed. Open Preferences to download.");
                return;
            }

            var config = new DiarizerConfig(
                DeviceIndex: ConfigManager.Current.DiarizerDeviceIndex,
                Debug: true
            );

            AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Starting Speaker Diarizer Process...\n");
            await _diarizerManager.StartAsync(config, pythonExe, scriptPath);
        }

        /// <summary>
        /// Bug 1: Pre-warm atom32 at app startup (if enabled in config).
        /// Starts the Python process and loads the model, then immediately soft-pauses the
        /// audio stream so no real capture occurs until the user starts a session.
        /// When Start Session is pressed, engine is already warm — resume_audio is near-instant.
        /// </summary>
        private async System.Threading.Tasks.Task PreWarmDiarizerAsync()
        {
            if (_diarizerManager != null) return; // Already running (e.g. opened from file)
            if (!ConfigManager.Current.EnableDiarizer) return;

            // Resolve paths
            var manifest = await PluginManifestService.LoadManifestAsync();
            var atom32Entry = manifest?.Atoms.FirstOrDefault(a => a.Id == "atom32");
            if (atom32Entry == null) return;

            string installDir = PluginManifestService.ResolveInstallDir(atom32Entry.InstallDir);
            string pythonExe = Path.Combine(installDir, ".venv", "Scripts", "python.exe");
            string scriptPath = Path.Combine(installDir, atom32Entry.EntryScript);

            if (!File.Exists(pythonExe) || !File.Exists(scriptPath)) return;

            _diarizerManager = new DiarizerProcessManager();
            _diarizerManager.OnLog += (msg) => AppendLog($"[DIARIZER] {msg}\n");
            _diarizerManager.OnEvent += HandleDiarizerEvent;

            var config = new DiarizerConfig(DeviceIndex: ConfigManager.Current.DiarizerDeviceIndex, Debug: true);

            AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Pre-warming engine in background (audio paused until session starts)...\n");

            try
            {
                // StartPreWarmedAsync: awaits ReadyEvent then issues pause_audio
                await _diarizerManager.StartPreWarmedAsync(config, pythonExe, scriptPath);
                WireDiarizerCallbacks();
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Engine pre-warm complete. Ready to start session instantly.\n");
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Pre-warm failed: {ex.Message}. Will try again at session start.\n");
                _diarizerManager?.Dispose();
                _diarizerManager = null;
            }
        }

        private async System.Threading.Tasks.Task ShutdownDiarizerAsync()
        {
            if (_diarizerManager != null)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Stopping Speaker Diarizer Process...\n");
                await _diarizerManager.StopAsync();
                _diarizerManager.Dispose();
                _diarizerManager = null;
                
                // P3.4: Clear speakers on shutdown
                _workspaceVm.NavPane?.Speakers.Clear();

                // Sync: sau khi shutdown, cập nhật lại NavPane state
                SyncNavPaneDiarizerState();
            }
        }

        /// <summary>
        /// Cập nhật trạng thái khả dụng của diarizer vào NavPane.
        /// Gọi sau mỗi workspace open, sau khi diarizer start/stop.
        /// Bug 2 fix: DocNav Speaker panel không hiển thị vì NavPane không biết diarizer chưa chạy
        /// (IsDiarizerAvailable mặc định là true → UI hiện blank list thay vì fallback message).
        /// </summary>
        private void SyncNavPaneDiarizerState()
        {
            if (_diarizerManager != null)
            {
                // Diarizer đang chạy → NavPane biết speakers sẽ đến
                // Không reset IsDiarizerAvailable để tránh flicker nếu đã có speakers
                return;
            }

            // Diarizer chưa start → hiện fallback message trong Speaker panel
            string reason = !ConfigManager.Current.EnableDiarizer
                ? "Tính năng Speaker Diarization bị tắt trong Preferences. Bật để sử dụng."
                : "Speaker Diarization chưa được khởi động.\nMở Overlay (nút phía trên) để kích hoạt tự động.";

            _workspaceVm.NavPane?.SetDiarizerUnavailable(reason);
        }


        /// <summary>
        /// Handle diarization events from atom32 process.
        /// Updates NavPane speaker list and caches latest speaker for commit injection.
        /// </summary>
        private void HandleDiarizerEvent(DiarizerEvent evt)
        {
            switch (evt)
            {
                case TimelineUpdateEvent tu:
                    // Update NavPane speaker list on UI thread
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _workspaceVm.NavPane?.SyncSpeakers(tu.Segments);
                    });
                    break;

                case RecognitionEvent re:
                    // Cache latest speaker for next commit and instantly sync with Doc Nav
                    _latestSpeakerUid = re.Uid;
                    _latestSpeakerDisplayName = re.DisplayName;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (!string.IsNullOrEmpty(re.Uid))
                            _workspaceVm.NavPane?.AddOrUpdateSpeaker(re.Uid, !string.IsNullOrEmpty(re.DisplayName) ? re.DisplayName : re.Uid);
                    });
                    break;

                case ReadyEvent:
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Engine ready.\n");
                    // Bug 2 fix: Mark NavPane available khi diarizer lên sóng
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _workspaceVm.NavPane?.SetDiarizerAvailable();
                    });
                    break;


                case ErrorEvent err:
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER ERROR] {err.Message}\n");
                    break;

                case StoppedEvent:
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Process stopped.\n");
                    break;

                case VolLevelEvent vol:
                    // Optional: could update volume meter UI in future
                    break;

                case SessionFlushedEvent flush:
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Session flushed. Speakers: {flush.UidMap.Count}\n");
                    break;

                case AudioPausedEvent:
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Audio stream paused (process alive).\n");
                    break;

                case AudioResumedEvent:
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Audio stream resumed.\n");
                    break;

                case MergeSuggestionsEvent suggestions:
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _workspaceVm.NavPane?.SetMergeSuggestions(suggestions.Suggestions);
                    });
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Merge suggestions: {suggestions.Suggestions.Count} pair(s).\n");
                    break;
            }
        }

        /// <summary>
        /// Wire NavPane callbacks to atom32 IPC after diarizer is initialized.
        /// Called once per fresh session start, not on soft-resume.
        /// </summary>
        private void WireDiarizerCallbacks()
        {
            var navPane = _workspaceVm.NavPane;
            if (navPane == null || _diarizerManager == null) return;

            // Rename: double-click edit in DocNav -> label_speaker IPC
            navPane.SpeakerRenameRequested = async (uid, newName) =>
            {
                await _diarizerManager.SendCommandAsync(new { cmd = "label_speaker", uid, display_name = newName });
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Renamed {uid} -> {newName}\n");
            };

            // Merge speakers: merge picker -> merge_speakers IPC
            navPane.SpeakerMergeRequested = async (uidSource, uidTarget) =>
            {
                await _diarizerManager.SendCommandAsync(new { cmd = "merge_speakers", uid_source = uidSource, uid_target = uidTarget });
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var toRemove = navPane.Speakers.FirstOrDefault(s => s.SpeakerKey == uidSource);
                    if (toRemove != null) navPane.Speakers.Remove(toRemove);
                });
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Merged {uidSource} into {uidTarget}\n");
            };

            // Reassign segment: segment picker -> reassign_segment IPC
            navPane.SegmentReassignRequested = async (oldUid, startSec, endSec, newUid) =>
            {
                await _diarizerManager.SendCommandAsync(new
                {
                    cmd = "reassign_segment",
                    old_uid = oldUid,
                    new_uid = newUid,
                    start_sec = startSec,
                    end_sec = endSec
                });
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DIARIZER] Reassigned {startSec:F1}-{endSec:F1}s: {oldUid} -> {newUid}\n");
            };

            // Dismiss suggestion -> dismiss_merge_suggestion IPC
            navPane.MergeSuggestionDismissRequested = async (pid1, pid2) =>
            {
                await _diarizerManager.SendCommandAsync(new { cmd = "dismiss_merge_suggestion", pid1, pid2 });
            };

            // Refresh button -> get_merge_suggestions IPC
            navPane.RefreshMergeSuggestionsRequested = async () =>
            {
                await _diarizerManager.SendCommandAsync(new { cmd = "get_merge_suggestions" });
            };
        }

        public AIService AIService => _aiService;
        public SegmentDisplayModel SegmentDisplayModel => _segmentDisplayModel;
        public m_mslc_overlay.core.Animation.FadeAnimationController FadeAnimationController => _fadeAnimationController;

        public bool IsTranslationEnabled
        {
            get => _isTranslationEnabled;
            set
            {
                _isTranslationEnabled = value;
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            }
        }

        public string ContextTopic
        {
            get => _contextTopic;
            set
            {
                _contextTopic = value;
                _aiService.ContextTopic = value;
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            }
        }

        private void OpenOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentOverlay == null || !_currentOverlay.IsVisible)
            {
                _currentOverlay = new FloatingTextOverlay(this);
                _currentOverlay.Show();
                // Diarizer lifecycle is managed by Session Start/Stop, NOT by overlay.
                // Overlay is display-only — it shows captions regardless of diarizer state.
            }
            else
            {
                _currentOverlay.Activate();
            }
        }

        private DebugWidget? _debugWidget;

        private void OnImportScriptMenuClick(object? sender, RoutedEventArgs e)
        {
            OnImportScriptRequested();
        }

        private void OpenDebugWidgetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_debugWidget == null || !_debugWidget.IsVisible)
            {
                _debugWidget = new DebugWidget(_pipeService);
                _debugWidget.OnInterruptRequested += () => {
                    _currentOverlay?.ClearQueueAndText();
                };
                _debugWidget.Show();
            }
            else
            {
                _debugWidget.Activate();
            }
        }

        private void OpenPaperSheetDemoBtn_Click(object sender, RoutedEventArgs e)
        {
            var demoWindow = new m_mslc_overlay.views.dialogs.PaperSheetDemoWindow();
            demoWindow.Show();
        }

        private void OpenTipTapDemoBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var win = new views.webview_demos.WebViewDemoWindow("TipTap Demo", "assets/web/tiptap.html");
            win.Show();
        }

        private void OpenQuillDemoBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var win = new views.webview_demos.WebViewDemoWindow("Quill Demo", "assets/web/quill.html");
            win.Show();
        }

        private void OpenMonacoDemoBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var win = new views.webview_demos.WebViewDemoWindow("Monaco Demo", "assets/web/monaco.html");
            win.Show();
        }

        private void ThemeToggleBtn_Click(object? sender, RoutedEventArgs e)
        {
            var tm = services.ThemeManager.Instance;
            // Cycle: System -> Light -> Dark -> System
            var next = tm.Mode switch
            {
                services.ThemeMode.System => services.ThemeMode.Light,
                services.ThemeMode.Light  => services.ThemeMode.Dark,
                services.ThemeMode.Dark   => services.ThemeMode.System,
                _                         => services.ThemeMode.System
            };
            tm.Apply(next);
            UpdateThemeIcon(next);
        }

        private void UpdateThemeIcon(services.ThemeMode mode)
        {
            var icon = this.FindControl<Material.Icons.Avalonia.MaterialIcon>("ThemeIcon");
            if (icon == null) return;
            icon.Kind = mode switch
            {
                services.ThemeMode.Light  => Material.Icons.MaterialIconKind.WeatherSunny,
                services.ThemeMode.Dark   => Material.Icons.MaterialIconKind.WeatherNight,
                services.ThemeMode.System => Material.Icons.MaterialIconKind.ThemeLightDark,
                _                         => Material.Icons.MaterialIconKind.ThemeLightDark
            };
        }

        private void PreferencesMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            var preferencesDialog = new m_mslc_overlay.views.dialogs.PreferencesDialog();
            preferencesDialog.ShowDialog(this);
        }

        private void ActiveExtractorCheckout_Click(object? sender, RoutedEventArgs e)
        {
            var updateDialog = new m_mslc_overlay.views.dialogs.ExtractorUpdateDialog();
            updateDialog.ShowDialog(this);
        }

        private void ChangeLanguage_Vi_Click(object? sender, RoutedEventArgs e)
        {
            LanguageManager.LoadLanguage("vi-VN");
            UpdateDynamicStrings();
        }

        private void ChangeLanguage_En_Click(object? sender, RoutedEventArgs e)
        {
            LanguageManager.LoadLanguage("en-US");
            UpdateDynamicStrings();
        }

        private void ResetOverlayPosition_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentOverlay != null && _currentOverlay.IsVisible)
            {
                var screen = _currentOverlay.Screens.ScreenFromWindow(_currentOverlay) ?? _currentOverlay.Screens.Primary;
                if (screen != null)
                {
                    var x = (screen.Bounds.Width - (int)_currentOverlay.Width) / 2;
                    var y = (screen.Bounds.Height - (int)_currentOverlay.Height) / 2;
                    _currentOverlay.Position = new Avalonia.PixelPoint(x, y);
                    ConfigManager.Current.OverlayPositionX = x;
                    ConfigManager.Current.OverlayPositionY = y;
                    ConfigManager.Save();
                }
            }
            else
            {
                ConfigManager.Current.OverlayPositionX = -1;
                ConfigManager.Current.OverlayPositionY = -1;
                ConfigManager.Save();
            }
        }

        private void ToggleOverlayLock_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentOverlay != null)
            {
                _currentOverlay.ToggleLock();
            }
            else
            {
                ConfigManager.Current.OverlayIsLocked = !ConfigManager.Current.OverlayIsLocked;
                ConfigManager.Save();
            }
        }

        public enum PanelPosition { Left, Right, Top, Bottom }
        public PanelPosition ConfiguredSidePanelPosition = PanelPosition.Right;
        public bool ConfiguredSidePanelTopmost = false;

        private SidePanelWindow? _sidePanelWindow;

        private void ToggleSidePanel_Click(object? sender, RoutedEventArgs e)
        {
            if (_sidePanelWindow != null && _sidePanelWindow.IsVisible)
            {
                _sidePanelWindow.Close();
                _sidePanelWindow = null;
            }
            else
            {
                var screen = this.Screens.ScreenFromWindow(this) ?? this.Screens.Primary;
                if (screen == null) return;

                var workArea = screen.WorkingArea;
                double scaling = screen.Scaling;
                double workAreaWidthDip = workArea.Width / scaling;
                double workAreaHeightDip = workArea.Height / scaling;

                _sidePanelWindow = new SidePanelWindow();
                _sidePanelWindow.Topmost = ConfiguredSidePanelTopmost;
                _sidePanelWindow.OnClosedAction = () => {
                    _sidePanelWindow = null;
                };

                if (this.WindowState == WindowState.FullScreen || this.WindowState == WindowState.Maximized)
                {
                    this.WindowState = WindowState.Normal;

                    switch (ConfiguredSidePanelPosition)
                    {
                        case PanelPosition.Left:
                            this.Position = new Avalonia.PixelPoint(workArea.X + workArea.Width / 2, workArea.Y);
                            this.Width = workAreaWidthDip / 2.0;
                            this.Height = workAreaHeightDip;
                            break;
                        case PanelPosition.Right:
                            this.Position = new Avalonia.PixelPoint(workArea.X, workArea.Y);
                            this.Width = workAreaWidthDip / 2.0;
                            this.Height = workAreaHeightDip;
                            break;
                        case PanelPosition.Top:
                            this.Position = new Avalonia.PixelPoint(workArea.X, workArea.Y + workArea.Height / 2);
                            this.Width = workAreaWidthDip;
                            this.Height = workAreaHeightDip / 2.0;
                            break;
                        case PanelPosition.Bottom:
                            this.Position = new Avalonia.PixelPoint(workArea.X, workArea.Y);
                            this.Width = workAreaWidthDip;
                            this.Height = workAreaHeightDip / 2.0;
                            break;
                    }
                }

                switch (ConfiguredSidePanelPosition)
                {
                    case PanelPosition.Left:
                        _sidePanelWindow.Position = new Avalonia.PixelPoint(workArea.X, workArea.Y);
                        _sidePanelWindow.Width = workAreaWidthDip / 2.0;
                        _sidePanelWindow.Height = workAreaHeightDip;
                        break;
                    case PanelPosition.Right:
                        _sidePanelWindow.Position = new Avalonia.PixelPoint(workArea.X + workArea.Width / 2, workArea.Y);
                        _sidePanelWindow.Width = workAreaWidthDip / 2.0;
                        _sidePanelWindow.Height = workAreaHeightDip;
                        break;
                    case PanelPosition.Top:
                        _sidePanelWindow.Position = new Avalonia.PixelPoint(workArea.X, workArea.Y);
                        _sidePanelWindow.Width = workAreaWidthDip;
                        _sidePanelWindow.Height = workAreaHeightDip / 2.0;
                        break;
                    case PanelPosition.Bottom:
                        _sidePanelWindow.Position = new Avalonia.PixelPoint(workArea.X, workArea.Y + workArea.Height / 2);
                        _sidePanelWindow.Width = workAreaWidthDip;
                        _sidePanelWindow.Height = workAreaHeightDip / 2.0;
                        break;
                }

                _sidePanelWindow.Show();
            }
        }

        private void InitializeHotkeys()
        {
            if (!ConfigManager.Current.EnableGlobalHotkeys) return;

            try
            {
                if (_hotkeyManager == null)
                {
                    _hotkeyManager = new HotkeyManager(this);
                    _hotkeyManager.Initialize();
                }

                if (ConfigManager.Current.Hotkeys != null)
                {
                    foreach (var kvp in ConfigManager.Current.Hotkeys)
                    {
                        var hotkey = kvp.Value;
                        if (!hotkey.IsGlobal || string.IsNullOrWhiteSpace(hotkey.KeyGesture)) continue;

                        Action? action = GetActionForId(hotkey.ActionId);
                        if (action != null)
                        {
                            _hotkeyManager.TryRegister(hotkey.ActionId, hotkey.KeyGesture, action, out _);
                        }
                    }
                }
                
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Global hotkeys manager initialized.\n");
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [ERROR] Failed to initialize global hotkeys: {ex.Message}\n");
            }
        }

        private void InitializeFocusKeys()
        {
            try
            {
                _focusKeyController = new FocusKeyController(this);

                if (ConfigManager.Current.Hotkeys != null)
                {
                    foreach (var kvp in ConfigManager.Current.Hotkeys)
                    {
                        var hotkey = kvp.Value;
                        if (hotkey.IsGlobal || string.IsNullOrWhiteSpace(hotkey.KeyGesture)) continue;

                        Action? action = GetActionForId(hotkey.ActionId);
                        if (action != null)
                        {
                            try
                            {
                                var gesture = Avalonia.Input.KeyGesture.Parse(hotkey.KeyGesture);
                                _focusKeyController.Register(gesture.Key, gesture.KeyModifiers, action);
                            }
                            catch { /* Ignore invalid parse */ }
                        }
                    }
                }

                // Keep some fallback keys if needed or map everything to config.
                // For simplicity, we just keep the config-based focus keys.
                // A-Z Keys (active only when no text box has focus)
                _focusKeyController.RegisterFallbackKey(Key.O, ToggleOverlay);
                _focusKeyController.RegisterFallbackKey(Key.T, ToggleTranslation);
                _focusKeyController.RegisterFallbackKey(Key.L, CycleLanguage);
                _focusKeyController.RegisterFallbackKey(Key.C, ClearOverlayText);

                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Focused window key controller initialized.\n");
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [ERROR] Failed to initialize focused key controller: {ex.Message}\n");
            }
        }
        
        private Action? GetActionForId(string actionId)
        {
            return actionId switch
            {
                "NewWorkspace" => () => OnNewWorkspaceMenuClick(null, null!),
                "OpenWorkspace" => () => OnOpenWorkspaceMenuClick(null, null!),
                "StartSession" => ToggleRecordingSession,
                "ToggleOverlay" => ToggleOverlay,
                "ToggleTranslate" => ToggleTranslation,
                "CycleLanguage" => CycleLanguage,
                "ClearText" => ClearOverlayText,
                "FontSizeUp" => () => ChangeOverlayFontSize(2.0),
                "FontSizeDown" => () => ChangeOverlayFontSize(-2.0),
                _ => null
            };
        }
        
        private void ToggleRecordingSession()
        {
            if (_workspaceVm.IsOpen)
            {
                if (_workspaceVm.IsRecording)
                    _workspaceVm.StopRecording();
                else
                    _workspaceVm.StartRecording();
            }
        }

        public void UpdateHotkeyRegistration()
        {
            if (ConfigManager.Current.EnableGlobalHotkeys)
            {
                if (_hotkeyManager == null)
                {
                    InitializeHotkeys();
                }
                else 
                {
                    // Re-register
                    _hotkeyManager.UnregisterAll();
                    InitializeHotkeys();
                }
            }
            else
            {
                if (_hotkeyManager != null)
                {
                    _hotkeyManager.Dispose();
                    _hotkeyManager = null;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Global hotkeys disabled.\n");
                }
            }
        }

        public void ToggleOverlay()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (_currentOverlay == null || !_currentOverlay.IsVisible)
                {
                    _currentOverlay = new FloatingTextOverlay(this);
                    _currentOverlay.Show();
                    // Diarizer is session-scoped; hotkey toggling overlay does not affect it.
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Floating Overlay opened.\n");
                }
                else
                {
                    _currentOverlay.Close();
                    _currentOverlay = null;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Floating Overlay closed.\n");
                }
            });
        }

        public void ToggleTranslation()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                IsTranslationEnabled = !IsTranslationEnabled;
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Translation toggled to: {IsTranslationEnabled}\n");
            });
        }

        public void CycleLanguage()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (!IsTranslationEnabled)
                {
                    _aiService.TargetLanguage = "Tiếng Việt";
                    IsTranslationEnabled = true;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Target language set to: Tiếng Việt\n");
                    _currentOverlay?.SetImmediateText(LanguageManager.GetString("Msg_LangVietnamese"));
                }
                else if (_aiService.TargetLanguage == "Tiếng Việt")
                {
                    _aiService.TargetLanguage = "Tiếng Nhật";
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Target language set to: Tiếng Nhật\n");
                    _currentOverlay?.SetImmediateText(LanguageManager.GetString("Msg_LangJapanese"));
                }
                else if (_aiService.TargetLanguage == "Tiếng Nhật")
                {
                    _aiService.TargetLanguage = "Tiếng Trung";
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Target language set to: Tiếng Trung\n");
                    _currentOverlay?.SetImmediateText(LanguageManager.GetString("Msg_LangChinese"));
                }
                else
                {
                    IsTranslationEnabled = false;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Target language set to: English (Raw Mode)\n");
                    _currentOverlay?.SetImmediateText(LanguageManager.GetString("Msg_LangEnglish"));
                }
            });
        }

        public void ClearOverlayText()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (_currentOverlay != null && _currentOverlay.IsVisible)
                {
                    _currentOverlay.ClearQueueAndText();
                    _segmentTracker.MarkOverlayReset();  // ATOM79: ATOM75 overlay reset hook
                    _revisionWindow.Reset();  // ATOM80: clear pending on manual overlay clear
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Overlay text cleared.\n");
                }
            });
        }

        public void ChangeOverlayFontSize(double delta)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (_currentOverlay != null && _currentOverlay.IsVisible)
                {
                    double newSize = Math.Clamp(_currentOverlay.OverlayFontSize + delta, 12.0, 40.0);
                    _currentOverlay.OverlayFontSize = newSize;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [HOTKEY] Font size changed to {newSize:F1}\n");
                }
            });
        }

        private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }

        private void UpdateResponsiveLayout(double width)
        {
            var grid = this.FindControl<Grid>("MainContentGrid");
            var sidebar = this.FindControl<Border>("SidebarBorder");
            if (sidebar != null && grid != null && grid.ColumnDefinitions.Count > 0)
            {
                if (width >= 1200)
                {
                    sidebar.IsVisible = true;
                    if (sidebar.Classes.Contains("Mini"))
                    {
                        sidebar.Classes.Remove("Mini");
                        _isAdjustingSidebar = true;
                        try
                        {
                            grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(_userSidebarWidth) };
                        }
                        finally
                        {
                            _isAdjustingSidebar = false;
                        }
                    }
                    else if (grid.ColumnDefinitions[0].Width.Value < 160)
                    {
                        _isAdjustingSidebar = true;
                        try
                        {
                            grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(_userSidebarWidth) };
                        }
                        finally
                        {
                            _isAdjustingSidebar = false;
                        }
                    }
                }
                else if (width >= 768)
                {
                    sidebar.IsVisible = true;
                    if (!sidebar.Classes.Contains("Mini"))
                    {
                        sidebar.Classes.Add("Mini");
                    }
                    _isAdjustingSidebar = true;
                    try
                    {
                        grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(60) };
                    }
                    finally
                    {
                        _isAdjustingSidebar = false;
                    }
                }
                else
                {
                    sidebar.IsVisible = false;
                    _isAdjustingSidebar = true;
                    try
                    {
                        grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(0) };
                    }
                    finally
                    {
                        _isAdjustingSidebar = false;
                    }
                }
            }

            // TranscriptViewport was removed — NavPane responsive control removed with it
        }

        private void SidebarBorder_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_isAdjustingSidebar) return;

            var sidebar = sender as Border;
            if (sidebar == null) return;

            var grid = this.FindControl<Grid>("MainContentGrid");
            if (grid == null || grid.ColumnDefinitions.Count == 0) return;

            double width = e.NewSize.Width;

            // Nếu đang ở Mini mode (collapsed)
            if (sidebar.Classes.Contains("Mini"))
            {
                // Nếu người dùng kéo mở rộng ra vượt quá ngưỡng 80px
                if (width > 80)
                {
                    _isAdjustingSidebar = true;
                    try
                    {
                        sidebar.Classes.Remove("Mini");
                        double targetWidth = Math.Max(180.0, Math.Min(width, 360.0));
                        _userSidebarWidth = targetWidth; // Ghi nhận kích thước mới được khôi phục
                        grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(targetWidth) };
                    }
                    finally
                    {
                        _isAdjustingSidebar = false;
                    }
                }
            }
            else // Đang ở Normal mode
            {
                // Ghi nhận lựa chọn kích thước của người dùng nếu nó hợp lệ
                if (width >= 160 && width <= 360)
                {
                    _userSidebarWidth = width;
                }

                // Nếu kéo nhỏ hơn MIN_VISIBLE (160px), tự động thu gọn về collapsed state (60px)
                if (width < 160)
                {
                    _isAdjustingSidebar = true;
                    try
                    {
                        if (!sidebar.Classes.Contains("Mini"))
                        {
                            sidebar.Classes.Add("Mini");
                        }
                        grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(60) };
                    }
                    finally
                    {
                        _isAdjustingSidebar = false;
                    }
                }
                else if (width > 360) // Giới hạn MAX_EXPAND là 360px
                {
                    _isAdjustingSidebar = true;
                    try
                    {
                        grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(360) };
                    }
                    finally
                    {
                        _isAdjustingSidebar = false;
                    }
                }
            }
        }

        private void ToggleSidebarBtn_Click(object? sender, RoutedEventArgs e)
        {
            var sidebar = this.FindControl<Border>("SidebarBorder");
            var grid = this.FindControl<Grid>("MainContentGrid");
            if (sidebar == null || grid == null || grid.ColumnDefinitions.Count == 0) return;

            _isAdjustingSidebar = true;
            try
            {
                if (sidebar.Classes.Contains("Mini"))
                {
                    // Bung ra Normal mode
                    sidebar.Classes.Remove("Mini");
                    grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(_userSidebarWidth) };
                }
                else
                {
                    // Thu gọn về Mini mode
                    if (!sidebar.Classes.Contains("Mini"))
                    {
                        sidebar.Classes.Add("Mini");
                    }
                    grid.ColumnDefinitions[0] = new ColumnDefinition { Width = new GridLength(60.0) };
                }
            }
            finally
            {
                _isAdjustingSidebar = false;
            }
        }

        private async void CheckEnvironmentMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            await m_mslc_overlay.views.dialogs.EnvironmentCheckDialog.ShowDiagnosticAsync(this);
        }

        private async System.Threading.Tasks.Task InitBootstrapAsync()
        {
            try
            {
                LoggerService.Log("[MainWindow] Starting bootstrap environment check...");

                // Run system environment diagnostic asynchronously
                var diag = await EnvironmentCheckerService.RunDiagnosticAsync();

                // Check Extractor binaries (Host.exe & Agent.dll)
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string extractorDir = AppPathHelper.GetExtractorDirectory();
                bool hasHost = File.Exists(Path.Combine(extractorDir, "Host.exe")) || File.Exists(Path.Combine(baseDir, "extractor", "Host.exe")) || File.Exists(Path.Combine(baseDir, "Host.exe"));
                bool hasAgent = File.Exists(Path.Combine(extractorDir, "Agent.dll")) || File.Exists(Path.Combine(baseDir, "extractor", "Agent.dll")) || File.Exists(Path.Combine(baseDir, "Agent.dll"));
                bool hasExtractor = hasHost && hasAgent;

                // Update UI thread controls
                Dispatcher.UIThread.Post(() => {
                    // 1. Python Runtime
                    var dotPy = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatusDotPython");
                    var txtPy = this.FindControl<TextBlock>("StatusTextPython");
                    if (dotPy != null)
                    {
                        dotPy.Fill = diag.HasPython ? Brush.Parse("#10B981") : Brush.Parse("#EF4444");
                        ToolTip.SetTip(dotPy, diag.HasPython ? $"Python: {diag.PythonVersion}\n{diag.PythonPath}" : "Python: Not Installed / Not in PATH");
                    }
                    if (txtPy != null && diag.HasPython)
                    {
                        txtPy.Text = $"Python ({diag.PythonVersion.Replace("Python ", "")})";
                    }

                    // 2. Live Caption
                    var dotCap = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatusDotCaption");
                    var txtCap = this.FindControl<TextBlock>("StatusTextCaption");
                    if (dotCap != null)
                    {
                        dotCap.Fill = diag.HasLiveCaptionsBinary ? Brush.Parse("#10B981") : Brush.Parse("#F59E0B");
                        ToolTip.SetTip(dotCap, diag.HasLiveCaptionsBinary 
                            ? $"LiveCaptions: {(diag.IsLiveCaptionsRunning ? "Running" : "Available")}\nPath: {diag.LiveCaptionsPath}"
                            : "LiveCaptions: Not Found");
                    }
                    if (txtCap != null)
                    {
                        txtCap.Text = diag.IsLiveCaptionsRunning 
                            ? $"Live caption (PID: {diag.LiveCaptionsPid})" 
                            : (diag.HasLiveCaptionsBinary ? "Live caption (Ready)" : "Live caption (Missing)");
                    }

                    // 3. CUDA Status
                    var dotCuda = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatusDotCuda");
                    var txtCuda = this.FindControl<TextBlock>("StatusTextCuda");
                    if (dotCuda != null)
                    {
                        dotCuda.Fill = diag.HasCuda ? Brush.Parse("#10B981") : Brush.Parse("#6B7280");
                        ToolTip.SetTip(dotCuda, diag.HasCuda ? $"CUDA: {diag.CudaVersion}\nGPU: {diag.GpuName}" : "CUDA: Not Available (CPU Mode)");
                    }
                    if (txtCuda != null)
                    {
                        txtCuda.Text = diag.HasCuda ? "CUDA (Active)" : "CUDA (CPU Mode)";
                    }

                    // 4. Extractor Module
                    var dotExt = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatusDotExtractor");
                    var txtExt = this.FindControl<TextBlock>("StatusTextExtractor");
                    if (dotExt != null)
                    {
                        dotExt.Fill = hasExtractor ? Brush.Parse("#10B981") : Brush.Parse("#EF4444");
                        ToolTip.SetTip(dotExt, hasExtractor ? "Extractor Module: Host.exe & Agent.dll Ready" : "Extractor Module: Host.exe or Agent.dll Missing");
                    }
                    if (txtExt != null)
                    {
                        txtExt.Text = hasExtractor ? "Extractor (Ready)" : "Extractor (Missing)";
                    }

                    // 5. Local Network
                    var dotNet = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatusDotNetwork");
                    if (dotNet != null)
                    {
                        dotNet.Fill = Brush.Parse("#10B981");
                        ToolTip.SetTip(dotNet, "Local Network Listener: 127.0.0.1 IPC Binding OK");
                    }

                    LoggerService.Log("[MainWindow] Status pane indicators updated from bootstrap diagnostic.");
                });
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[MainWindow] Bootstrap environment check error: {ex.Message}");
            }
        }
    }
}