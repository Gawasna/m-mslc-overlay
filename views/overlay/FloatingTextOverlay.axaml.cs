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
    private readonly System.Collections.Generic.List<string> _displayedSentences = new System.Collections.Generic.List<string>();
    private readonly m_mslc_overlay.core.Animation.SlideAnimationController _slideAnimationController = new m_mslc_overlay.core.Animation.SlideAnimationController();

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

    private int GetTotalLength(System.Collections.Generic.List<string> list)
    {
        int total = 0;
        foreach (var s in list)
        {
            total += s.Length;
        }
        return total;
    }

    private void UpdateBaseText()
    {
        bool needsSlide = false;
        // Giới hạn hiển thị: tối đa 3 câu hoặc tổng độ dài các câu đã hiển thị không quá 300 ký tự
        if (_displayedSentences.Count > 3 || GetTotalLength(_displayedSentences) > 300)
        {
            if (_displayedSentences.Count > 0)
            {
                needsSlide = true;
            }
        }

        if (needsSlide)
        {
            int totalLen = GetTotalLength(_displayedSentences);
            m_mslc_overlay.services.LoggerService.Log($"[FloatingTextOverlay] Overflow: {_displayedSentences.Count} sentences / {totalLen} chars → SlideUp");
            _ = _slideAnimationController.AnimateSlideUpAsync(DisplayTextBlock, OverlayFontSize * 1.5, () => {
                while (_displayedSentences.Count > 3 || GetTotalLength(_displayedSentences) > 300)
                {
                    if (_displayedSentences.Count > 0) _displayedSentences.RemoveAt(0);
                    else break;
                }
                
                if (_displayedSentences.Count > 0)
                    _baseText = string.Join("  ", _displayedSentences) + "  ";
                else
                    _baseText = "";
                DisplayTextBlock.Text = _baseText;
            });
            return;
        }

        if (_displayedSentences.Count > 0)
        {
            _baseText = string.Join("  ", _displayedSentences) + "  ";
        }
        else
        {
            _baseText = "";
        }
    }

    public void SetImmediateText(string text)
    {
        if (_typewriterTimer != null)
        {
            _typewriterTimer.Stop();
        }

        // Dọn dẹp hàng đợi và các câu đã hiển thị (vì đây là thông báo trực tiếp từ hệ thống)
        while (_sentenceQueue.TryDequeue(out _)) { }
        _displayedSentences.Clear();
        _baseText = "";
        _currentSentence = "";
        _typewriterIndex = 0;
        _delayTicks = 0;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            DisplayTextBlock.Text = text;
            TextScrollViewer.ScrollToEnd();
        });
    }

    public void SetStreamingText(string partialText)
    {
        if (_typewriterTimer != null && _typewriterTimer.IsEnabled)
        {
            _typewriterTimer.Stop();
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            DisplayTextBlock.Text = _baseText + partialText;
            TextScrollViewer.ScrollToEnd();
        });
    }

    public void AddFinalText(string finalText)
    {
        if (string.IsNullOrWhiteSpace(finalText)) return;

        if (_typewriterTimer != null && _typewriterTimer.IsEnabled)
        {
            _typewriterTimer.Stop();
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            _displayedSentences.Add(finalText.Trim());
            m_mslc_overlay.services.LoggerService.Log($"[Render] AddFinalText | sentences:{_displayedSentences.Count} | '{finalText.Trim().Substring(0, Math.Min(40, finalText.Trim().Length))}'");
            UpdateBaseText();
            DisplayTextBlock.Text = _baseText;
            TextScrollViewer.ScrollToEnd();
        });
    }

    /// <summary>
    /// ATOM76/ATOM80: Hot-replace the last displayed sentence with new text.
    /// Used by RevisionWindow to merge short fragments into the preceding translation.
    /// No-op if _displayedSentences is empty.
    /// </summary>
    public void ReplaceLastText(string newText)
    {
        if (string.IsNullOrWhiteSpace(newText)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            string oldText = _displayedSentences.Count > 0
                ? _displayedSentences[_displayedSentences.Count - 1]
                : "(empty)";

            if (_displayedSentences.Count > 0)
            {
                _displayedSentences[_displayedSentences.Count - 1] = newText.Trim();
            }
            else
            {
                _displayedSentences.Add(newText.Trim());
            }

            string oldSnippet = oldText.Length > 30 ? oldText.Substring(0, 30) + "..." : oldText;
            string newSnippet = newText.Trim().Length > 30 ? newText.Trim().Substring(0, 30) + "..." : newText.Trim();
            m_mslc_overlay.services.LoggerService.Log($"[Render] ReplaceLastText | '{oldSnippet}' → '{newSnippet}'");

            UpdateBaseText();
            if (_mainWindow != null && _mainWindow.FadeAnimationController != null)
            {
                m_mslc_overlay.services.LoggerService.Log($"[Render] FadeAnimationController triggered");
                _ = _mainWindow.FadeAnimationController.AnimateReplaceAsync(DisplayTextBlock, _baseText);
            }
            else
            {
                DisplayTextBlock.Text = _baseText;
            }
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
        _displayedSentences.Clear();
        _baseText = "";
        _currentSentence = "";
        _typewriterIndex = 0;
        _delayTicks = 0;
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
        _displayedSentences.Clear();
        _baseText = "";
        _currentSentence = "";
        _typewriterIndex = 0;
        _delayTicks = 0;
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
            if (queueLength > 6)
            {
                _typewriterIndex = _currentSentence.Length;
            }
            else
            {
                int charStep = 1;
                if (queueLength > 4) charStep = 3;
                else if (queueLength > 1) charStep = 2;
                _typewriterIndex = Math.Min(_typewriterIndex + charStep, _currentSentence.Length);
            }

            DisplayTextBlock.Text = _baseText + _currentSentence.Substring(0, _typewriterIndex);

            if (_typewriterIndex == _currentSentence.Length || _typewriterIndex % 12 == 0)
            {
                TextScrollViewer.ScrollToEnd();
            }

            if (_typewriterIndex == _currentSentence.Length)
            {
                _delayTicks = queueLength > 0 ? 5 : 15; // 150ms vs 450ms
            }
            return;
        }

        if (_sentenceQueue.TryDequeue(out string? nextSentence) && nextSentence != null)
        {
            if (!string.IsNullOrEmpty(_currentSentence))
            {
                _displayedSentences.Add(_currentSentence.Trim());
            }

            UpdateBaseText();

            _currentSentence = nextSentence;
            _typewriterIndex = 0;

            string snippet = nextSentence.Length > 40 ? nextSentence.Substring(0, 40) + "..." : nextSentence;
            m_mslc_overlay.services.LoggerService.Log($"[Render] Typewriter dequeue | queue:{_sentenceQueue.Count} remaining | '{snippet}'");
        }
    }
}
