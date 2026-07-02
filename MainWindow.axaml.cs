using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using m_mslc_overlay.views.components;
using m_mslc_overlay.views.overlay;
using m_mslc_overlay.services;
using m_mslc_overlay.core;

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
        private readonly RevisionWindowService _revisionWindow = new RevisionWindowService();
        private SystemMonitor _sysMonitor;
        private DispatcherTimer _resourceTimer;
        private DispatcherTimer _uiUpdateTimer;
        private HotkeyManager? _hotkeyManager;
        private FocusKeyController? _focusKeyController;

        private bool _isTranslationEnabled = true;
        private string _contextTopic = "Game/Phim";

        private readonly object _translationLock = new object();
        private string _translationBuffer = "";
        private string _translationDisplayBuffer = "";
        private bool _isTranslationDirty = false;

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

        public MainWindow()
        {
            LoggerService.Initialize();
            InitializeComponent();
            _hiderService = new AppContainerHiderService();
            _pipeService = new LiveCaptionPipeService();
            _injectorService = new InjectorService();
            _aiService = new AIService();
            _shortSentenceBuffer = new ShortSentenceBuffer();

            // ATOM50: Short sentence buffer merges fragments (≤3 words) with the next
            // long sentence before forwarding to translation, avoiding wasteful API calls
            // for isolated tokens like "but", "So", "Because", "I".
            _shortSentenceBuffer.OnFlush += (mergedText) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    if (IsTranslationEnabled)
                    {
                        lock (_translationLock) {
                            _translationBuffer = "";
                        }
                        // ATOM81: enqueue via priority queue instead of direct async call
                        _aiService.EnqueueTranslation(mergedText, "SoftCommit");
                    }
                    else
                    {
                        if (_currentOverlay != null && _currentOverlay.IsVisible)
                        {
                            if (_currentOverlay.UseTypewriter)
                                _currentOverlay.EnqueueText(mergedText);
                            else
                                _currentOverlay.AddFinalText(mergedText);
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
            _pipeService.OnFinalSentenceReceived += (meta) => {
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

                // ATOM50: route through ShortSentenceBuffer — translation fires
                // only when OnFlush is triggered (either by merge or timeout).
                _shortSentenceBuffer.Feed(meta.Text, meta.Reason);
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
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            };

            // 6. Cập nhật log lỗi của Pipe Server
            _pipeService.OnError += (errorMsg) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [ERROR] {errorMsg}\n");
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            };

            this.Closing += (s, e) => {
                _shortSentenceBuffer.Flush();   // ATOM50: flush any pending before shutdown
                _shortSentenceBuffer.Dispose();
                _aiService.Dispose();           // ATOM81: dispose priority queue and http client
                _revisionWindow.Dispose();      // ATOM80
                _hiderService.Dispose();
                _pipeService.Dispose();
                _resourceTimer.Stop();
                _uiUpdateTimer.Stop();
                _hotkeyManager?.Dispose();
                _focusKeyController?.Dispose();
                OfflineTranslationServerManager.StopServer();
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
        }

        private void AppendLog(string logLine)
        {
            lock(_logLock) {
                _rawLogs.Add(logLine);
                if (_rawLogs.Count > 100) _rawLogs.RemoveAt(0);
                _isLogDirty = true;
            }
            LoggerService.Log(logLine);
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
            bool hasHost = File.Exists(Path.Combine(baseDir, "extractor", "Host.exe")) || File.Exists(Path.Combine(baseDir, "Host.exe"));
            bool hasAgent = File.Exists(Path.Combine(baseDir, "extractor", "Agent.dll")) || File.Exists(Path.Combine(baseDir, "Agent.dll"));
            
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

        public AIService AIService => _aiService;

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
            }
            else
            {
                _currentOverlay.Activate();
            }
        }

        private DebugWidget? _debugWidget;

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
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (_currentOverlay == null || !_currentOverlay.IsVisible)
                {
                    _currentOverlay = new FloatingTextOverlay(this);
                    _currentOverlay.Show();
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
    }
}