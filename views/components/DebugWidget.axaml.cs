using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Text.Json;
using m_mslc_overlay.core;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.components
{
    public partial class DebugWidget : Window
    {
        private SentenceSplitter _splitter;
        private LiveCaptionPipeService? _pipeService;
        public event Action? OnInterruptRequested;

        public DebugWidget()
        {
            InitializeComponent();
            _splitter = new SentenceSplitter();
            Log("Debug Widget Initialized (Offline mode).");
        }

        public DebugWidget(LiveCaptionPipeService pipeService) : this()
        {
            _pipeService = pipeService;
            
            // Hook to live pipe events
            _pipeService.OnStatusChanged += Pipe_OnStatusChanged;
            _pipeService.OnPartialCaptionReceived += Pipe_OnPartialCaptionReceived;
            _pipeService.OnFinalSentenceReceived += Pipe_OnFinalSentenceReceived;
            _pipeService.OnError += Pipe_OnError;

            this.Closing += (s, e) => {
                _pipeService.OnStatusChanged -= Pipe_OnStatusChanged;
                _pipeService.OnPartialCaptionReceived -= Pipe_OnPartialCaptionReceived;
                _pipeService.OnFinalSentenceReceived -= Pipe_OnFinalSentenceReceived;
                _pipeService.OnError -= Pipe_OnError;
            };

            Log("Hooked into LiveCaptionPipeService.", "#00FF88");
        }

        private void Pipe_OnStatusChanged(string status) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Log($"[PIPE] {status}", "#FFFF00"));

        private void Pipe_OnPartialCaptionReceived(string text) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Log($"[PIPE-PARTIAL] {text}", "#00FFFF"));

        private void Pipe_OnFinalSentenceReceived(string text) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Log($"[PIPE-FINAL] {text}", "#00FF00"));

        private void Pipe_OnError(string err) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Log($"[PIPE-ERROR] {err}", "#FF4444"));

        private void InterruptBtn_Click(object sender, RoutedEventArgs e)
        {
            OnInterruptRequested?.Invoke();
            Log("[INTERRUPT] Xuất lệnh ngắt Render Queue thành công.", "#FF5555");
        }

        private void Log(string message, string color = "#AAAAAA")
        {
            var textBlock = new TextBlock
            {
                Text = $"[{DateTime.Now:HH:mm:ss.fff}] {message}",
                Foreground = SolidColorBrush.Parse(color),
                FontFamily = FontFamily.Parse("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 2)
            };
            
            LogPanel.Children.Add(textBlock);
            LogScroll.ScrollToEnd();
        }

        private void SendJsonBtn_Click(object sender, RoutedEventArgs e)
        {
            ProcessJson(JsonInput.Text ?? "");
        }

        private void SendFinalBtn_Click(object sender, RoutedEventArgs e)
        {
            // Trích xuất chuỗi hiện tại, giả lập là Final luôn
            try
            {
                var doc = JsonDocument.Parse(JsonInput.Text ?? "{}");
                var text = doc.RootElement.GetProperty("text").GetString();
                var json = $"{{\"text\":\"{text}\",\"is_final\":true,\"bytes\":0,\"ts_ms\":0}}";
                ProcessJson(json);
            }
            catch
            {
                var json = $"{{\"text\":\"{JsonInput.Text}\",\"is_final\":true,\"bytes\":0,\"ts_ms\":0}}";
                ProcessJson(json);
            }
        }

        private void ProcessJson(string jsonStr)
        {
            try
            {
                var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                
                string text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                bool isFinal = root.TryGetProperty("is_final", out var finalProp) && finalProp.GetBoolean();
                
                Log($"Received: {(isFinal ? "[F]" : "[~]")} {text}", "#00FFFF");

                var sentences = _splitter.ExtractNewSentences(text, isFinal);
                foreach (var sentence in sentences)
                {
                    Log($"SENTENCE EXTRACTED -> {sentence}", "#00FF00");
                }
            }
            catch (Exception ex)
            {
                Log($"JSON Parse Error: {ex.Message}", "#FF4444");
            }
        }

        private void ResetStateBtn_Click(object sender, RoutedEventArgs e)
        {
            _splitter.Reset();
            Log("SentenceSplitter state reset.", "#FFAA00");
        }

        private void ClearLogsBtn_Click(object sender, RoutedEventArgs e)
        {
            LogPanel.Children.Clear();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
