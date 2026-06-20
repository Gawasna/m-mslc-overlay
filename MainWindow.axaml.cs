using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Diagnostics;
using System.IO;
using m_mslc_overlay.views.components;
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

        private string _translationBuffer = "";

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
            
            _aiService.ContextTopic = TopicInput.Text ?? "Game/Phim";
            TopicInput.TextChanged += (s, e) => {
                _aiService.ContextTopic = TopicInput.Text ?? "Game/Phim";
            };

            // 1. Nhận luồng text thô partial (đang nhận dạng) từ Extractor
            _pipeService.OnPartialCaptionReceived += (txt) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    LiveRawText.Text = string.IsNullOrWhiteSpace(txt) ? LanguageManager.GetString("Status_NoActiveSpeech") : txt;

                    // Đồng bộ hành vi Floating Overlay giống hệt LiveRawText (hiển thị partial thô trực tiếp đè lên câu cũ)
                    if (_currentOverlay != null && _currentOverlay.IsVisible)
                    {
                        _currentOverlay.SetImmediateText(txt);
                    }
                });
            };

            // 2. Nhận câu thô hoàn chỉnh (final) từ Extractor
            _pipeService.OnFinalSentenceReceived += (txt) => {
                if (string.IsNullOrWhiteSpace(txt)) return;

                // Định dạng timestamp cho câu thô
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string logLine = $"[{timestamp}] {LanguageManager.GetString("Log_EnglishPrefix")}: {txt}\n";

                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    // In dữ liệu text thô đưa từ extractor lên cửa sổ chính
                    RawTextLog.Text += logLine;
                    
                    // Cuộn xuống cuối
                    RawTextLog.CaretIndex = RawTextLog.Text.Length;
                    LogScrollViewer.ScrollToEnd();

                    // Nếu bật dịch thuật AI
                    if (TranslateToggle.IsChecked == true)
                    {
                        _translationBuffer = ""; // Reset buffer bản dịch mới
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
                if (TranslateToggle.IsChecked == true)
                {
                    _translationBuffer += tokenStr;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        if (_currentOverlay != null && _currentOverlay.IsVisible)
                        {
                            // Thay thế trực tiếp hiển thị bằng bản dịch tiếng Việt chạy từ từ (typewriter feedback) đè lên câu cũ
                            _currentOverlay.SetImmediateText(_translationBuffer);
                        }
                    });
                }
            };

            // 4. Khi hoàn thành dịch 1 câu hoàn chỉnh
            _aiService.OnTranslationCompleted += (fullSentence) => {
                if (TranslateToggle.IsChecked == true)
                {
                    // In bản dịch tiếng Việt sang RawTextLog để dễ debug song song
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    string logLine = $"[{timestamp}] {LanguageManager.GetString("Log_VietnamesePrefix")}: {fullSentence}\n-----------------------------------\n";

                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        RawTextLog.Text += logLine;
                        RawTextLog.CaretIndex = RawTextLog.Text.Length;
                        LogScrollViewer.ScrollToEnd();

                        if (_currentOverlay != null && _currentOverlay.IsVisible)
                        {
                            // Đảm bảo hiển thị bản dịch hoàn chỉnh chính xác
                            _currentOverlay.SetImmediateText(fullSentence);
                        }
                    });
                }
            };

            // 5. Cập nhật log trạng thái của Pipe Server
            _pipeService.OnStatusChanged += (statusMsg) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    RawTextLog.Text += $"[{timestamp}] [SYSTEM] {statusMsg}\n";
                    LogScrollViewer.ScrollToEnd();
                });
            };

            // 6. Cập nhật log lỗi của Pipe Server
            _pipeService.OnError += (errorMsg) => {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    RawTextLog.Text += $"[{timestamp}] [ERROR] {errorMsg}\n";
                    LogScrollViewer.ScrollToEnd();
                });
            };

            this.Closing += (s, e) => {
                _hiderService.Dispose();
                _pipeService.Dispose();
            };

            // Dò tìm PID lúc khởi động (nếu đã bật sẵn Live Captions)
            DetectTargetProcess();
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
        }

        private async void InjectBtn_Click(object sender, RoutedEventArgs e)
        {
            DetectTargetProcess();

            uint pid = _hiderService.TargetProcessId;
            if (pid == 0)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                RawTextLog.Text += $"[{timestamp}] [WARNING] LiveCaptions chưa chạy! Vui lòng khởi động Windows Live Captions trước.\n";
                return;
            }

            string ts = DateTime.Now.ToString("HH:mm:ss");
            RawTextLog.Text += $"[{ts}] [SYSTEM] Starting DLL injection into PID {pid}...\n";

            bool success = await _injectorService.InjectAsync(pid);

            if (success)
            {
                HookStatusDot.Fill = SolidColorBrush.Parse("#00FF88");
                HookStatusText.Text = "Injected";
                
                string nowTs = DateTime.Now.ToString("HH:mm:ss");
                RawTextLog.Text += $"[{nowTs}] [SYSTEM] DLL injected successfully. Starting Named Pipe listener...\n";
                
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
                RawTextLog.Text += $"[{nowTs}] [ERROR] Injection failed or Administrator permission was denied.\n";
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

        private SidePanelWindow? _sidePanelWindow;
        private WindowState _previousMainWindowState;
        private double _previousWidth;
        private double _previousHeight;
        private Avalonia.PixelPoint _previousPosition;

        private void ToggleSidePanel_Click(object? sender, RoutedEventArgs e)
        {
            if (_sidePanelWindow != null && _sidePanelWindow.IsVisible)
            {
                _sidePanelWindow.Close();
            }
            else
            {
                var screen = this.Screens.ScreenFromWindow(this) ?? this.Screens.Primary;
                if (screen == null) return;

                var workArea = screen.WorkingArea;
                double scaling = screen.Scaling;
                double workAreaWidthDip = workArea.Width / scaling;
                double workAreaHeightDip = workArea.Height / scaling;
                
                _previousMainWindowState = this.WindowState;
                _previousWidth = double.IsNaN(this.Width) ? this.Bounds.Width : this.Width;
                _previousHeight = double.IsNaN(this.Height) ? this.Bounds.Height : this.Height;
                _previousPosition = this.Position;

                _sidePanelWindow = new SidePanelWindow();
                _sidePanelWindow.OnClosedAction = () => {
                    if (_sidePanelWindow != null) {
                        _sidePanelWindow = null;
                        this.WindowState = _previousMainWindowState;
                        if (this.WindowState == WindowState.Normal)
                        {
                            this.Position = _previousPosition;
                            this.Width = _previousWidth;
                            this.Height = _previousHeight;
                        }
                    }
                };

                if (this.WindowState == WindowState.FullScreen || this.WindowState == WindowState.Maximized)
                {
                    this.WindowState = WindowState.Normal;
                    this.Position = new Avalonia.PixelPoint(workArea.X, workArea.Y);
                    this.Width = workAreaWidthDip / 2.0;
                    this.Height = workAreaHeightDip;
                }

                _sidePanelWindow.Position = new Avalonia.PixelPoint(workArea.X + workArea.Width / 2, workArea.Y);
                _sidePanelWindow.Width = workAreaWidthDip / 2.0;
                _sidePanelWindow.Height = workAreaHeightDip;

                _sidePanelWindow.Show();
            }
        }
    }
}