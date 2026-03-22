using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace m_mslc_overlay.views.components;

public partial class FloatingTextOverlay : Window
{
    private const string LongSampleText = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Dự án đang test tính năng tự động giãn chữ và gõ phím theo phong cách typewriter. Nếu bạn kéo các viền, giao diện vẫn sẽ tự do thay đổi kích thước ngang dọc và tự động dồn (wrap) chữ cho phù hợp không gian mà hoàn toàn uyển chuyển! Đây là dòng giả lập dữ liệu dài từ bản dịch voice capture...";

    private CancellationTokenSource? _typewriterCts;

    public FloatingTextOverlay()
    {
        InitializeComponent();
        this.Opened += (s, e) => StartTypewriterEffect();
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
        StartTypewriterEffect();
    }

    private async void StartTypewriterEffect()
    {
        _typewriterCts?.Cancel();
        _typewriterCts = new CancellationTokenSource();
        var token = _typewriterCts.Token;

        DisplayTextBlock.Text = "";

        try
        {
            foreach (char c in LongSampleText)
            {
                if (token.IsCancellationRequested) break;
                
                DisplayTextBlock.Text += c;
                TextScrollViewer.ScrollToEnd(); // Tự động cuộn
                
                // Add tiny delay to simulate typewriter logic
                await Task.Delay(20, token);
            }
        }
        catch (TaskCanceledException)
        {
            // Ignore
        }
    }
}
