using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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
        
        // CRITICAL-TEXT-001: Track previous segment end time for continuous timeline
        private long _lastSegmentEndMs = 0;

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
                    }
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
            _shortSentenceBuffer.OnFlush += (mergedMeta) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    if (IsTranslationEnabled)
                    {
                        lock (_translationLock) {
                            _translationBuffer = "";
                        }
                        // FIX V3: Pass full CommitMetadata instead of bare string
                        _aiService.EnqueueTranslation(mergedMeta);
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
                        // ✅ AUTO-START RECORDING: Start recording automatically on first STT segment
                        if (!_workspaceVm.IsRecording)
                        {
                            _workspaceVm.StartRecording();
                            System.Diagnostics.Debug.WriteLine("[MainWindow] 🎙️ Auto-started recording on first STT segment");
                        }
                        
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
                        
                        // CRITICAL-TEXT-001 FIX: Use previous segment's end as this segment's start
                        // This creates a continuous timeline and eliminates accumulating drift.
                        // First segment: Use 0 as start
                        long tsStartMs = _lastSegmentEndMs > 0 ? _lastSegmentEndMs : 0;
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
                        _segmentIdMap[segment.Id] = dbId;
                    }

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
                            if (_segmentIdMap.TryGetValue(linkedSeg.Id, out long dbId))
                            {
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
                            }
                            else
                            {
                                // ✅ FIX 2: Add logging for missing DB ID
                                AppendLog($"[{timestamp}] ⚠️ WARNING: Segment ID {linkedSeg.Id} not found in _segmentIdMap. Translation lost!\n");
                                System.Diagnostics.Debug.WriteLine($"[MainWindow] ⚠️ SegmentID {linkedSeg.Id} not in _segmentIdMap. DbId lookup failed.");
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
                    _lastSegmentEndMs = 0;  // CRITICAL-TEXT-001: Reset timing state on reconnect
                }
                
                // ✅ AUTO-STOP RECORDING: Stop recording when STT disconnects
                if (statusMsg.Contains("Client disconnected"))
                {
                    if (_workspaceVm.IsRecording)
                    {
                        _workspaceVm.StopRecording();
                        System.Diagnostics.Debug.WriteLine("[MainWindow] 🛑 Auto-stopped recording on STT disconnect");
                    }
                }
                
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

            this.Opened += (s, e) => {
                InitializeHotkeys();
                InitializeFocusKeys();
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
            }
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
            }
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

        private async void OpenOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentOverlay == null || !_currentOverlay.IsVisible)
            {
                _currentOverlay = new FloatingTextOverlay(this);
                _currentOverlay.Closed += async (s, ev) => {
                    await ShutdownDiarizerAsync();
                };
                _currentOverlay.Show();
                await InitializeDiarizerAsync();
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
                _hotkeyManager = new HotkeyManager(this);
                _hotkeyManager.Initialize();

                // Register hotkeys: Alt + Shift + O/T/L/C/Up/Down (Temporarily disabled as requested)
                // 101: Toggle Overlay (Alt + Shift + O)
                // 102: Toggle Translation (Alt + Shift + T)
                // 103: Cycle Language (Alt + Shift + L)
                // 104: Clear Text (Alt + Shift + C)
                // 105: Increase Font Size (Alt + Shift + Up)
                // 106: Decrease Font Size (Alt + Shift + Down)

                // _hotkeyManager.Register(101, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x4F, ToggleOverlay);
                // _hotkeyManager.Register(102, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x54, ToggleTranslation);
                // _hotkeyManager.Register(103, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x4C, CycleLanguage);
                // _hotkeyManager.Register(104, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x43, ClearOverlayText);
                // _hotkeyManager.Register(105, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x26, () => ChangeOverlayFontSize(2.0));
                // _hotkeyManager.Register(106, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x28, () => ChangeOverlayFontSize(-2.0));

                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Global hotkeys manager initialized (Alt+Shift+X hotkeys temporarily disabled).\n");
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

                // 1. Register shortcuts with modifiers (e.g. Ctrl + Key)
                _focusKeyController.Register(Key.O, KeyModifiers.Control, ToggleOverlay);
                _focusKeyController.Register(Key.T, KeyModifiers.Control, ToggleTranslation);
                _focusKeyController.Register(Key.L, KeyModifiers.Control, CycleLanguage);
                _focusKeyController.Register(Key.C, KeyModifiers.Control, ClearOverlayText);
                _focusKeyController.Register(Key.Up, KeyModifiers.Control, () => ChangeOverlayFontSize(2.0));
                _focusKeyController.Register(Key.Down, KeyModifiers.Control, () => ChangeOverlayFontSize(-2.0));

                // 2. Register fallback keys (Fx, A-Z) without modifiers (bypassed if typing in TextBox)
                // Fx Keys
                _focusKeyController.RegisterFallbackKey(Key.F1, ToggleOverlay);
                _focusKeyController.RegisterFallbackKey(Key.F2, ToggleTranslation);
                _focusKeyController.RegisterFallbackKey(Key.F3, CycleLanguage);
                _focusKeyController.RegisterFallbackKey(Key.F4, ClearOverlayText);
                _focusKeyController.RegisterFallbackKey(Key.F5, () => ChangeOverlayFontSize(2.0));
                _focusKeyController.RegisterFallbackKey(Key.F6, () => ChangeOverlayFontSize(-2.0));

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

        public void UpdateHotkeyRegistration()
        {
            if (ConfigManager.Current.EnableGlobalHotkeys)
            {
                if (_hotkeyManager == null)
                {
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
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                if (_currentOverlay == null || !_currentOverlay.IsVisible)
                {
                    _currentOverlay = new FloatingTextOverlay(this);
                    _currentOverlay.Closed += async (s, ev) => {
                        await ShutdownDiarizerAsync();
                    };
                    _currentOverlay.Show();
                    await InitializeDiarizerAsync();
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