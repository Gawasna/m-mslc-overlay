using Avalonia;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

using m_mslc_overlay.services;

namespace m_mslc_overlay.views.overlay;

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
    private DispatcherTimer? _typewriterTimer;
    private int _typewriterIndex = 0;
    private string _currentSentence = "";
    private string _baseText = "";
    private int _delayTicks = 0;
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
        if (_typewriterTimer != null)
        {
            _typewriterTimer.Stop();
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            DisplayTextBlock.Text = text;
            TextScrollViewer.ScrollToEnd();
        });
    }

    private void Window_PointerPressed(object sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            var pos = e.GetPosition(this);
            double width = this.Bounds.Width;
            double height = this.Bounds.Height;
            double margin = 8.0;

            bool isLeft = pos.X < margin;
            bool isRight = pos.X > width - margin;
            bool isTop = pos.Y < margin;
            bool isBottom = pos.Y > height - margin;

            if (isLeft && isTop) this.BeginResizeDrag(WindowEdge.NorthWest, e);
            else if (isRight && isTop) this.BeginResizeDrag(WindowEdge.NorthEast, e);
            else if (isLeft && isBottom) this.BeginResizeDrag(WindowEdge.SouthWest, e);
            else if (isRight && isBottom) this.BeginResizeDrag(WindowEdge.SouthEast, e);
            else if (isLeft) this.BeginResizeDrag(WindowEdge.West, e);
            else if (isRight) this.BeginResizeDrag(WindowEdge.East, e);
            else if (isTop) this.BeginResizeDrag(WindowEdge.North, e);
            else if (isBottom) this.BeginResizeDrag(WindowEdge.South, e);
            else
            {
                this.BeginMoveDrag(e);
            }
        }
    }

    private void Window_PointerMoved(object sender, Avalonia.Input.PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        double width = this.Bounds.Width;
        double height = this.Bounds.Height;
        double margin = 8.0;

        bool isLeft = pos.X < margin;
        bool isRight = pos.X > width - margin;
        bool isTop = pos.Y < margin;
        bool isBottom = pos.Y > height - margin;

        if (isLeft && isTop) this.Cursor = new Cursor(StandardCursorType.TopLeftCorner);
        else if (isRight && isTop) this.Cursor = new Cursor(StandardCursorType.TopRightCorner);
        else if (isLeft && isBottom) this.Cursor = new Cursor(StandardCursorType.BottomLeftCorner);
        else if (isRight && isBottom) this.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        else if (isLeft || isRight) this.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        else if (isTop || isBottom) this.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        else this.Cursor = new Cursor(StandardCursorType.Arrow);
    }

    private void Close_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _typewriterTimer?.Stop();
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
        if (_typewriterTimer != null && _typewriterTimer.IsEnabled) return;

        _typewriterTimer?.Stop();
        _typewriterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _typewriterTimer.Tick += OnTypewriterTick;
        _typewriterTimer.Start();

        DisplayTextBlock.Text = "";
    }

    private void OnTypewriterTick(object? sender, EventArgs e)
    {
        if (_delayTicks > 0)
        {
            _delayTicks--;
            return;
        }

        int queueLength = _sentenceQueue.Count;

        if (_typewriterIndex < _currentSentence.Length)
        {
            if (queueLength > 4)
            {
                _typewriterIndex = _currentSentence.Length;
            }
            else
            {
                int charStep = queueLength > 2 ? 4 : (queueLength > 0 ? 2 : 1);
                _typewriterIndex = Math.Min(_typewriterIndex + charStep, _currentSentence.Length);
            }

            DisplayTextBlock.Text = _baseText + _currentSentence.Substring(0, _typewriterIndex);

            if (_typewriterIndex == _currentSentence.Length || _typewriterIndex % 12 == 0)
            {
                TextScrollViewer.ScrollToEnd();
            }

            if (_typewriterIndex == _currentSentence.Length)
            {
                _delayTicks = queueLength > 0 ? 3 : 12; // 90ms vs 360ms
            }
            return;
        }

        if (_sentenceQueue.TryDequeue(out string? nextSentence) && nextSentence != null)
        {
            int currentLength = DisplayTextBlock.Text?.Length ?? 0;
            if (currentLength > 400) DisplayTextBlock.Text = "";

            _baseText = DisplayTextBlock.Text ?? "";
            _currentSentence = nextSentence;
            _typewriterIndex = 0;
        }
    }
}
