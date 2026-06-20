using Avalonia;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using m_mslc_overlay.services;

namespace m_mslc_overlay.views.components;

public partial class FloatingTextOverlay : Window
{
    public static readonly StyledProperty<double> OverlayFontSizeProperty =
        AvaloniaProperty.Register<FloatingTextOverlay, double>(nameof(OverlayFontSize), defaultValue: 20.0);

    public double OverlayFontSize
    {
        get => GetValue(OverlayFontSizeProperty);
        set => SetValue(OverlayFontSizeProperty, value);
    }

    public static readonly StyledProperty<IBrush> OverlayBackgroundProperty =
        AvaloniaProperty.Register<FloatingTextOverlay, IBrush>(nameof(OverlayBackground), defaultValue: SolidColorBrush.Parse("#CC202020"));

    public IBrush OverlayBackground
    {
        get => GetValue(OverlayBackgroundProperty);
        set => SetValue(OverlayBackgroundProperty, value);
    }

    private readonly MainWindow? _mainWindow;
    private CancellationTokenSource? _typewriterCts;
    private readonly AppContainerHiderService _hiderService = new AppContainerHiderService();

    public bool UseTypewriter { get; set; } = true;

    public FloatingTextOverlay()
    {
        InitializeComponent();
        
        // Kích hoạt khi bắt đầu hiển thị Float Widget
        this.Opened += (s, e) => 
        {
            StartTypewriterPump();
            
            // Xâm nhập Hệ điều hành và đánh cắp Window Container (LiveCaptions), tàng hình ngay lập tức 
            _hiderService.HideTargetApp("LiveCaptions");
        };

        // Kích hoạt trả lại nguyên trạng Target App khi Float Widget bị tắt đi
        this.Closed += (s, e) =>
        {
            _hiderService.RestoreTargetApp();
            _hiderService.Dispose();
        };
    }

    public FloatingTextOverlay(MainWindow mainWindow) : this()
    {
        _mainWindow = mainWindow;
    }

    public void ReHideTargetApp()
    {
        // Khôi phục an toàn cửa sổ cũ (nếu có)
        _hiderService.RestoreTargetApp();
        // Tìm và ẩn tiến trình Live Captions mới
        _hiderService.HideTargetApp("LiveCaptions");
    }

    public void SetImmediateText(string text)
    {
        // Hủy typewriter pump để tránh xung đột ghi đè lên text hiển thị trực tiếp
        _typewriterCts?.Cancel();
        _typewriterCts = null;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            DisplayTextBlock.Text = text;
            TextScrollViewer.ScrollToEnd();
        });
    }

    private void Window_PointerPressed(object sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginMoveDrag(e);
        }
    }

    private void Close_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _typewriterCts?.Cancel();
        this.Close();
    }

    private void ResetPosition_Click(object? sender, RoutedEventArgs e)
    {
        var screen = Screens.Primary;
        if (screen != null)
        {
            var x = (screen.Bounds.Width - (int)this.Width) / 2;
            var y = (screen.Bounds.Height - (int)this.Height) / 2;
            this.Position = new Avalonia.PixelPoint(x, y);
        }
    }

    private void SetLanguage_Vietnamese_Click(object? sender, RoutedEventArgs e)
    {
        if (_mainWindow != null)
        {
            _mainWindow.AIService.TargetLanguage = "Tiếng Việt";
            _mainWindow.IsTranslationEnabled = true;
            DisplayTextBlock.Text = LanguageManager.GetString("Msg_LangVietnamese");
        }
    }

    private void SetLanguage_Japanese_Click(object? sender, RoutedEventArgs e)
    {
        if (_mainWindow != null)
        {
            _mainWindow.AIService.TargetLanguage = "Tiếng Nhật";
            _mainWindow.IsTranslationEnabled = true;
            DisplayTextBlock.Text = LanguageManager.GetString("Msg_LangJapanese");
        }
    }

    private void SetLanguage_Chinese_Click(object? sender, RoutedEventArgs e)
    {
        if (_mainWindow != null)
        {
            _mainWindow.AIService.TargetLanguage = "Tiếng Trung";
            _mainWindow.IsTranslationEnabled = true;
            DisplayTextBlock.Text = LanguageManager.GetString("Msg_LangChinese");
        }
    }

    private void SetLanguage_English_Click(object? sender, RoutedEventArgs e)
    {
        if (_mainWindow != null)
        {
            _mainWindow.IsTranslationEnabled = false;
            DisplayTextBlock.Text = LanguageManager.GetString("Msg_LangEnglish");
        }
    }

    private void SetFontSize_Small_Click(object? sender, RoutedEventArgs e)
    {
        OverlayFontSize = 16.0;
    }

    private void SetFontSize_Medium_Click(object? sender, RoutedEventArgs e)
    {
        OverlayFontSize = 20.0;
    }

    private void SetFontSize_Large_Click(object? sender, RoutedEventArgs e)
    {
        OverlayFontSize = 24.0;
    }

    private void SetFontSize_ExtraLarge_Click(object? sender, RoutedEventArgs e)
    {
        OverlayFontSize = 28.0;
    }

    private void SetBgOpacity_100_Click(object? sender, RoutedEventArgs e)
    {
        OverlayBackground = SolidColorBrush.Parse("#FF202020");
    }

    private void SetBgOpacity_80_Click(object? sender, RoutedEventArgs e)
    {
        OverlayBackground = SolidColorBrush.Parse("#CC202020");
    }

    private void SetBgOpacity_60_Click(object? sender, RoutedEventArgs e)
    {
        OverlayBackground = SolidColorBrush.Parse("#99202020");
    }

    private void SetBgOpacity_40_Click(object? sender, RoutedEventArgs e)
    {
        OverlayBackground = SolidColorBrush.Parse("#66202020");
    }

    private void SetEffect_Typewriter_Click(object? sender, RoutedEventArgs e)
    {
        UseTypewriter = true;
        DisplayTextBlock.Text = LanguageManager.GetString("Msg_EffectTypewriter");
    }

    private void SetEffect_Instant_Click(object? sender, RoutedEventArgs e)
    {
        UseTypewriter = false;
        DisplayTextBlock.Text = LanguageManager.GetString("Msg_EffectInstant");
    }

    private void TestTypewriter_Click(object? sender, RoutedEventArgs e)
    {
        DisplayTextBlock.Text = "";
        while (_sentenceQueue.TryDequeue(out _)) { }
        StartTypewriterPump();
        EnqueueText(LanguageManager.GetString("Msg_TestTypewriterSentence"));
    }

    private void Help_Click(object? sender, RoutedEventArgs e)
    {
        DisplayTextBlock.Text = LanguageManager.GetString("Msg_HelpText");
    }

    private System.Collections.Concurrent.ConcurrentQueue<string> _sentenceQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();

    public void EnqueueText(string text)
    {
        _sentenceQueue.Enqueue(text);
    }

    public void ClearQueueAndText()
    {
        while (_sentenceQueue.TryDequeue(out _)) { }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            DisplayTextBlock.Text = "";
        });
    }

    public void StartTypewriterPump()
    {
        // Tránh chạy nhiều task song song nếu pump đang hoạt động
        if (_typewriterCts != null && !_typewriterCts.IsCancellationRequested) return;

        _typewriterCts?.Cancel();
        _typewriterCts?.Dispose();
        _typewriterCts = new CancellationTokenSource();
        var token = _typewriterCts.Token;

        DisplayTextBlock.Text = "";

        // Chạy ngầm một vòng lặp vĩnh viễn lúc cửa sổ đang bật để tiêu thụ Queue
        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_sentenceQueue.TryDequeue(out string? currentSentence) && currentSentence != null)
                    {
                        string baseText = "";
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                            int currentLength = DisplayTextBlock.Text?.Length ?? 0;
                            // Xoá màn hình nếu đang giữ quá nhiều chữ tránh tràn RAM
                            if (currentLength > 400) DisplayTextBlock.Text = "";
                            
                            baseText = DisplayTextBlock.Text ?? "";
                        });

                        int queueLength = _sentenceQueue.Count;

                        // EMERGENCY MODE: Nếu tồn đọng quá 5 câu, huỷ animation đánh máy, phọt toàn bộ text ra ngay
                        if (queueLength > 4)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                                DisplayTextBlock.Text = baseText + currentSentence;
                                TextScrollViewer.ScrollToEnd();
                            });
                            await Task.Delay(50, token); // Delay tối thiểu
                            continue;
                        }

                        // TÍNH TOÁN CẤP SỐ NHÂN TỐC ĐỘ:
                        // Nếu queue > 2, nhảy hẳn 3 chữ cái mỗi nhịp. Nếu thưa dần thì nhảy 1-2 chữ.
                        int charStep = queueLength > 2 ? 4 : (queueLength > 0 ? 2 : 1);
                        int delayMs = queueLength > 0 ? 10 : 30; 
                        int sentenceDelay = queueLength > 0 ? 100 : 400;

                        for (int i = 0; i < currentSentence.Length; i += charStep)
                        {
                            if (token.IsCancellationRequested) break;
                            
                            int len = Math.Min(i + charStep, currentSentence.Length);
                            string textToRender = baseText + currentSentence.Substring(0, len);

                            // Dùng Post (Fire-and-forget) thay vì InvokeAsync(đợi kết quả) để giảm thắt cổ chai UI Thread
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                                DisplayTextBlock.Text = textToRender;
                                if (len == currentSentence.Length || len % 12 == 0)
                                {
                                    TextScrollViewer.ScrollToEnd();
                                }
                            });
                            
                            await Task.Delay(delayMs, token);
                        }
                        
                        await Task.Delay(sentenceDelay, token);
                    }
                    else
                    {
                        // Khi rỗng Queue, đợi một chu kỳ ngắn
                        await Task.Delay(100, token);
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
        });
    }
}
