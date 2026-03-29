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

    public void ClearQueueAndText()
    {
        while (_sentenceQueue.TryDequeue(out _)) { }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            DisplayTextBlock.Text = "";
        });
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
                        string baseText = "";
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                            int currentLength = DisplayTextBlock.Text?.Length ?? 0;
                            // Xoá màn hình nếu đang giữ quá nhiều chữ tránh tràn RAM
                            if (currentLength > 400) DisplayTextBlock.Text = "";
                            else if (currentLength > 0) DisplayTextBlock.Text += "\n";
                            
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
