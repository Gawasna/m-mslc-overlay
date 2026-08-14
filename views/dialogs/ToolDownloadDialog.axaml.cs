using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MMslcOverlay.Services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class ToolDownloadDialog : Window
    {
        private readonly CancellationTokenSource _cts = new();
        private FfmpegBootstrapService.EnsureResult? _result;

        public FfmpegBootstrapService.EnsureResult? Result => _result;

        public ToolDownloadDialog()
        {
            InitializeComponent();
            Opened += async (_, _) => await RunAsync();
            Closed += (_, _) =>
            {
                try { _cts.Cancel(); } catch { /* ignore */ }
                _cts.Dispose();
            };
        }

        private void CancelBtn_Click(object? sender, RoutedEventArgs e)
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            StatusText.Text = "Đang hủy...";
            CancelBtn.IsEnabled = false;
        }

        private async Task RunAsync()
        {
            var progress = new Progress<double>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressBar.Value = Math.Clamp(p, 0, 100);
                });
            });
            var status = new Progress<string>(s =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText.Text = s;
                });
            });

            try
            {
                _result = await FfmpegBootstrapService.EnsureReadyAsync(progress, status, _cts.Token);
            }
            catch (Exception ex)
            {
                _result = new FfmpegBootstrapService.EnsureResult(false, null, ex.Message, false);
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_result?.Success == true)
                {
                    StatusText.Text = _result.DidDownload
                        ? "Tải xong. Đang tiếp tục xuất video..."
                        : "Công cụ đã sẵn sàng.";
                    ProgressBar.Value = 100;
                    await Task.Delay(400);
                }
                else
                {
                    StatusText.Text = _result?.ErrorMessage ?? "Thất bại.";
                    CancelBtn.Content = "Đóng";
                    CancelBtn.IsEnabled = true;
                    CancelBtn.Click -= CancelBtn_Click;
                    CancelBtn.Click += (_, _) => Close();
                    return;
                }
                Close();
            });
        }

        public static async Task<FfmpegBootstrapService.EnsureResult> EnsureWithUiAsync(Window owner)
        {
            if (FfmpegBootstrapService.IsReady())
            {
                string? path = FfmpegBootstrapService.ResolveExistingPath();
                return new FfmpegBootstrapService.EnsureResult(true, path, null, DidDownload: false);
            }

            bool ok = await MessageDialog.ShowAsync(
                owner,
                "Cần tải công cụ xử lý video",
                "Để ghép phụ đề vào video, app cần tải công cụ xử lý video (khoảng 80–100 MB).\n\n"
                + "Chỉ tải một lần, lần sau dùng lại. Bạn có muốn tải ngay không?",
                showCancel: true,
                okText: "Tải ngay",
                cancelText: "Hủy");

            if (!ok)
            {
                return new FfmpegBootstrapService.EnsureResult(false, null,
                    "Bạn đã hủy tải công cụ xử lý video.", DidDownload: false);
            }

            var dlg = new ToolDownloadDialog();
            await dlg.ShowDialog(owner);
            return dlg.Result
                   ?? new FfmpegBootstrapService.EnsureResult(false, null, "Không rõ kết quả tải.", false);
        }
    }
}
