using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using m_mslc_overlay.views.components;
using m_mslc_overlay.views.overlay;
using m_mslc_overlay.services;

namespace m_mslc_overlay
{
    public partial class MainWindow : Window
    {
        private FloatingTextOverlay? _currentOverlay;
        private AppContainerHiderService _hiderService;
        private LiveCaptionPipeService _pipeService;
        private InjectorService _injectorService;
        private AIService _aiService;
        private SystemMonitor _sysMonitor;
        private DispatcherTimer _resourceTimer;
        private DispatcherTimer _uiUpdateTimer;
        private HotkeyManager? _hotkeyManager;

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
            InitializeComponent();
            _hiderService = new AppContainerHiderService();
            _pipeService = new LiveCaptionPipeService();
            _injectorService = new InjectorService();
            _aiService = new AIService();
            
            _sysMonitor = new SystemMonitor();
            _resourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _resourceTimer.Tick += OnResourceTimerTick;
            _resourceTimer.Start();
            
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiUpdateTimer.Tick += OnUiUpdateTimerTick;
            _uiUpdateTimer.Start();
            
            _aiService.ContextTopic = TopicInput.Text ?? "Game/Phim";
            TopicInput.TextChanged += (s, e) => {
                _aiService.ContextTopic = TopicInput.Text ?? "Game/Phim";
                UpdateDynamicStrings();
            };

            // 1. Nhận luồng text thô partial (đang nhận dạng) từ Extractor
            _pipeService.OnPartialCaptionReceived += (txt) => {
                _lastPartialCaption = txt;
                _isPartialCaptionDirty = true;
            };

            // 2. Nhận câu thô hoàn chỉnh (final) từ Extractor
            _pipeService.OnFinalSentenceReceived += (txt) => {
                if (string.IsNullOrWhiteSpace(txt)) return;

                // Định dạng timestamp cho câu thô
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string logLine = $"[{timestamp}] {LanguageManager.GetString("Log_EnglishPrefix")}: {txt}\n";
                AppendLog(logLine);

                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    // Nếu bật dịch thuật AI
                    if (TranslateToggle.IsChecked == true)
                    {
                        lock(_translationLock) {
                            _translationBuffer = ""; // Reset buffer bản dịch mới
                        }
                        // Chạy nền tác vụ gọi AI để tránh block thread UI
                        _ = _aiService.TranslateSentenceAsync(txt);
                    }
                    else
                    {
                        // Nếu tắt dịch thuật (Raw Mode), cập nhật câu final hoàn chỉnh
                        if (_currentOverlay != null && _currentOverlay.IsVisible)
                        {
                            if (_currentOverlay.UseTypewriter)
                            {
                                _currentOverlay.EnqueueText(txt);
                            }
                            else
                            {
                                _currentOverlay.SetImmediateText(txt);
                            }
                        }
                    }
                });
            };

            // 3. Nhận các token dịch từ AI
            _aiService.OnTranslationTokenReceived += (tokenStr) => {
                // Kiểm tra UI thread không lock, chỉ set cờ
                bool isTranslated = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    isTranslated = TranslateToggle.IsChecked == true;
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
            _aiService.OnTranslationCompleted += (fullSentence) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    if (TranslateToggle.IsChecked == true)
                    {
                        // In bản dịch tiếng Việt sang RawTextLog để dễ debug song song
                        string timestamp = DateTime.Now.ToString("HH:mm:ss");
                        string logLine = $"[{timestamp}] {LanguageManager.GetString("Log_VietnamesePrefix")}: {fullSentence}\n-----------------------------------\n";
                        AppendLog(logLine);

                        if (_currentOverlay != null && _currentOverlay.IsVisible)
                        {
                            if (ConfigManager.Current.TranslationEngine == "DeepL API")
                            {
                                if (_currentOverlay.UseTypewriter)
                                {
                                    _currentOverlay.StartTypewriterPump();
                                    _currentOverlay.EnqueueText(fullSentence + " ");
                                }
                                else
                                {
                                    _currentOverlay.SetImmediateText(fullSentence);
                                }
                            }
                            else
                            {
                                // Streaming engines fallback
                                _currentOverlay.SetImmediateText(fullSentence);
                            }
                        }
                    }
                });
            };

            // 5. Cập nhật log trạng thái của Pipe Server
            _pipeService.OnStatusChanged += (statusMsg) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [SYSTEM] {statusMsg}\n");
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            };

            // 6. Cập nhật log lỗi của Pipe Server
            _pipeService.OnError += (errorMsg) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[{timestamp}] [ERROR] {errorMsg}\n");
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateDynamicStrings);
            };

            this.Closing += (s, e) => {
                _hiderService.Dispose();
                _pipeService.Dispose();
                _resourceTimer.Stop();
                _uiUpdateTimer.Stop();
                _hotkeyManager?.Dispose();
            };

            this.Opened += (s, e) => {
                InitializeHotkeys();
            };

            // Dò tìm PID lúc khởi động (nếu đã bật sẵn Live Captions)
            DetectTargetProcess();
        }

        private void AppendLog(string logLine)
        {
            lock(_logLock) {
                _rawLogs.Add(logLine);
                if (_rawLogs.Count > 100) _rawLogs.RemoveAt(0);
                _isLogDirty = true;
            }
        }

        private void OnUiUpdateTimerTick(object? sender, EventArgs e)
        {
            if (_isLogDirty)
            {
                lock(_logLock) {
                    RawTextLog.Text = string.Join("", _rawLogs);
                    RawTextLog.CaretIndex = RawTextLog.Text.Length;
                    LogScrollViewer.ScrollToEnd();
                    _isLogDirty = false;
                }
            }

            if (_isPartialCaptionDirty)
            {
                LiveRawText.Text = string.IsNullOrWhiteSpace(_lastPartialCaption) ? LanguageManager.GetString("Status_NoActiveSpeech") : _lastPartialCaption;
                if (_currentOverlay != null && _currentOverlay.IsVisible && TranslateToggle.IsChecked != true)
                {
                    _currentOverlay.SetImmediateText(_lastPartialCaption);
                }
                _isPartialCaptionDirty = false;
            }

            if (_isTranslationDirty && TranslateToggle.IsChecked == true)
            {
                string displayTxt;
                lock(_translationLock) {
                    displayTxt = _translationDisplayBuffer;
                }
                if (_currentOverlay != null && _currentOverlay.IsVisible)
                {
                    _currentOverlay.SetImmediateText(displayTxt);
                }
                _isTranslationDirty = false;
            }
        }

        private void OnResourceTimerTick(object? sender, EventArgs e)
        {
            var metrics = _sysMonitor.GetMetrics();
            ResourceUsageText.Text = $"SYS: {metrics.sysCpu:F1}% CPU {metrics.sysRamMb:F0}MB | APP: {metrics.appCpu:F1}% CPU {metrics.appRamMb:F0}MB";
        }

        private void DetectTargetProcess()
        {
            uint pid = _hiderService.PreFindTargetProcessId("LiveCaptions");
            if (pid != 0)
            {
                _currentHookState = HookState.Detected;
            }
            else
            {
                _currentHookState = HookState.Waiting;
            }
            UpdateDynamicStrings();
        }

        private void UpdateDynamicStrings()
        {
            uint pid = _hiderService.TargetProcessId;
            if (pid == 0)
            {
                pid = _hiderService.PreFindTargetProcessId("LiveCaptions");
            }

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

            if (string.IsNullOrWhiteSpace(LiveRawText.Text) || 
                LiveRawText.Text == "No active speech detected." || 
                LiveRawText.Text == "Chưa phát hiện giọng nói hoạt động." || 
                LiveRawText.Text.StartsWith("["))
            {
                LiveRawText.Text = LanguageManager.GetString("Status_NoActiveSpeech");
            }

            // Cập nhật thông tin AI Model & Topic
            string topic = string.IsNullOrWhiteSpace(TopicInput.Text) ? "None" : TopicInput.Text;
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
            get => TranslateToggle.IsChecked ?? false;
            set => Avalonia.Threading.Dispatcher.UIThread.Post(() => TranslateToggle.IsChecked = value);
        }

        public string ContextTopic
        {
            get => TopicInput.Text ?? "Game/Phim";
            set => Avalonia.Threading.Dispatcher.UIThread.Post(() => TopicInput.Text = value);
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

                // Register hotkeys: Alt + Shift + O/T/L/C/Up/Down
                // 101: Toggle Overlay (Alt + Shift + O)
                // 102: Toggle Translation (Alt + Shift + T)
                // 103: Cycle Language (Alt + Shift + L)
                // 104: Clear Text (Alt + Shift + C)
                // 105: Increase Font Size (Alt + Shift + Up)
                // 106: Decrease Font Size (Alt + Shift + Down)

                _hotkeyManager.Register(101, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x4F, ToggleOverlay);
                _hotkeyManager.Register(102, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x54, ToggleTranslation);
                _hotkeyManager.Register(103, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x4C, CycleLanguage);
                _hotkeyManager.Register(104, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x43, ClearOverlayText);
                _hotkeyManager.Register(105, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x26, () => ChangeOverlayFontSize(2.0));
                _hotkeyManager.Register(106, HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT, 0x28, () => ChangeOverlayFontSize(-2.0));

                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] Global hotkeys initialized.\n");
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [ERROR] Failed to initialize global hotkeys: {ex.Message}\n");
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