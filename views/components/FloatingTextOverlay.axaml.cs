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
    private const string LongSampleText = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Dự án đang test tính năng tự động giãn chữ và gõ phím theo phong cách typewriter. Nếu bạn kéo các viền, giao diện vẫn sẽ tự do thay đổi kích thước ngang dọc và tự động dồn (wrap) chữ cho phù hợp không gian mà hoàn toàn uyển chuyển! Đây là dòng giả lập dữ liệu dài từ bản dịch voice capture...";

    private CancellationTokenSource? _typewriterCts;
    private readonly AppContainerHiderService _hiderService = new AppContainerHiderService();

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

    private void Window_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        // Cancel typewriter if interacting (optional, but requested to fix CPU spikes on rapid clicks causing massive Tasks)
        // Note: the original code had multiple non-cancelled Tasks if clicked rapidly.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginMoveDrag(e);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _typewriterCts?.Cancel();
        this.Close();
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        // Replay giờ đây sẽ xoá chữ cũ nhưng queue vẫn giữ. 
        DisplayTextBlock.Text = "";
    }

    private ConcurrentQueue<string> _sentenceQueue = new ConcurrentQueue<string>();

    public void EnqueueText(string text)
    {
        _sentenceQueue.Enqueue(text);
    }

    private void StartTypewriterPump()
    {
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
                        // Giới hạn buffer hiển thị để tránh sập RAM
                        var currentLength = 0;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                            currentLength = DisplayTextBlock.Text?.Length ?? 0;
                            if (currentLength > 500) DisplayTextBlock.Text = "";
                            else if (currentLength > 0) DisplayTextBlock.Text += "\n";
                        });

                        string baseText = "";
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { baseText = DisplayTextBlock.Text ?? ""; });

                        // Tính toán tốc độ gõ (Adaptive Speed)
                        int queueLength = _sentenceQueue.Count;
                        int delayMs = queueLength > 2 ? 10 : (queueLength == 1 ? 25 : 45);

                        for (int i = 0; i < currentSentence.Length; i++)
                        {
                            if (token.IsCancellationRequested) break;
                            
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                                DisplayTextBlock.Text = baseText + currentSentence.Substring(0, i + 1);
                                if (i % 3 == 0 || i == currentSentence.Length - 1)
                                {
                                    TextScrollViewer.ScrollToEnd();
                                }
                            });
                            
                            await Task.Delay(delayMs, token);
                        }
                        
                        // Chờ 1 chút để giãn cách từng câu
                        await Task.Delay(300, token);
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
