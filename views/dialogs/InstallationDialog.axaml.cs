using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Text;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class InstallationDialog : Window
    {
        private readonly string _modelId;
        private readonly string _modelOutputDir;
        private readonly StringBuilder _logs = new StringBuilder();
        
        private double _targetProgress = 0;
        private readonly Avalonia.Threading.DispatcherTimer? _progressTimer;

        // Constructor không tham số cho Avalonia XAML loader
        public InstallationDialog()
        {
            InitializeComponent();
            _modelId = "";
            _modelOutputDir = "";
        }

        public InstallationDialog(string modelId, string modelOutputDir) : this()
        {
            _modelId = modelId;
            _modelOutputDir = modelOutputDir;

            if (ModelNameText != null)
            {
                ModelNameText.Text = $"Mô hình: {modelId}";
            }

            // Đăng ký sự kiện
            OfflineTranslationInstaller.OnLogReceived += OnInstallerLogReceived;
            OfflineTranslationInstaller.OnProgressChanged += OnInstallerProgressChanged;
            OfflineTranslationInstaller.OnInstallationCompleted += OnInstallerCompleted;

            this.Closed += OnDialogClosed;

            // Khởi chạy timer cập nhật mượt mà (linear interpolation)
            _progressTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(20) // ~50 FPS
            };
            _progressTimer.Tick += OnProgressTimerTick;
            _progressTimer.Start();

            // Bắt đầu cài đặt
            _ = StartInstallationAsync();
        }

        private async System.Threading.Tasks.Task StartInstallationAsync()
        {
            await OfflineTranslationInstaller.StartInstallAsync(_modelId, _modelOutputDir);
        }

        private void OnProgressTimerTick(object? sender, EventArgs e)
        {
            if (ProgressBar == null) return;

            double current = ProgressBar.Value;
            if (current < _targetProgress)
            {
                double diff = _targetProgress - current;
                // Tăng mượt từ từ
                double step = Math.Max(0.4, diff * 0.08);
                ProgressBar.Value = Math.Min(_targetProgress, current + step);
            }
            else if (current > _targetProgress)
            {
                ProgressBar.Value = _targetProgress;
            }
        }

        private void UpdateStepText(string prefix, string message, Avalonia.Media.IBrush colorBrush)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (CurrentStepText != null)
                {
                    CurrentStepText.Text = $"{prefix} {message}";
                    CurrentStepText.Foreground = colorBrush;
                }
            });
        }

        private void OnInstallerLogReceived(string logLine)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                _logs.AppendLine(logLine);
                if (LogBox != null)
                {
                    LogBox.Text = _logs.ToString();
                    LogBox.CaretIndex = LogBox.Text.Length;
                }
            });
        }

        private void OnInstallerProgressChanged(double val)
        {
            _targetProgress = val;

            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (StatusText != null)
                {
                    if (val < 10)
                    {
                        StatusText.Text = "Đang khởi động quy trình...";
                        UpdateStepText("[▶]", "Khởi động cài đặt...", Avalonia.Media.Brushes.DarkOrange);
                    }
                    else if (val < 20)
                    {
                        StatusText.Text = "Đang kiểm tra Python trên hệ thống...";
                        UpdateStepText("[▶]", "Bước 1/5: Kiểm tra môi trường Python hệ thống", Avalonia.Media.Brushes.DarkOrange);
                    }
                    else if (val < 30)
                    {
                        StatusText.Text = "Đang đồng bộ hóa các tệp tin cấu hình...";
                        UpdateStepText("[▶]", "Bước 2/5: Đồng bộ hóa các tệp tin cấu hình và script", Avalonia.Media.Brushes.DarkOrange);
                    }
                    else if (val < 50)
                    {
                        StatusText.Text = "Đang thiết lập môi trường ảo Python (venv)...";
                        UpdateStepText("[▶]", "Bước 3/5: Tạo môi trường ảo cách ly (venv)", Avalonia.Media.Brushes.DarkOrange);
                    }
                    else if (val < 75)
                    {
                        StatusText.Text = "Đang cài đặt các thư viện phụ thuộc (pip install)...";
                        UpdateStepText("[▶]", "Bước 4/5: Cài đặt dependencies (FastAPI, CTranslate2, PyTorch)", Avalonia.Media.Brushes.DarkOrange);
                    }
                    else if (val < 100)
                    {
                        StatusText.Text = "Đang tải xuống mô hình AI và thực hiện lượng tử hóa (INT8)...";
                        UpdateStepText("[▶]", "Bước 5/5: Tải và lượng tử hóa mô hình AI (INT8)", Avalonia.Media.Brushes.DarkOrange);
                    }
                }
            });
        }

        private void OnInstallerCompleted(bool success, string msg)
        {
            _progressTimer?.Stop();

            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (ProgressBar != null)
                {
                    ProgressBar.Value = success ? 100 : _targetProgress;
                }

                if (CancelBtn != null) CancelBtn.IsEnabled = false;
                if (CloseBtn != null) CloseBtn.IsEnabled = true;

                if (StatusText != null)
                {
                    StatusText.Text = success ? "Cài đặt thành công!" : $"Cài đặt thất bại: {msg}";
                }

                if (success)
                {
                    UpdateStepText("[✔]", "Cài đặt hoàn tất thành công!", Avalonia.Media.Brushes.Green);
                }
                else
                {
                    UpdateStepText("[✘]", $"Cài đặt thất bại: {msg}", Avalonia.Media.Brushes.Red);
                }
            });
        }

        private void CancelBtn_Click(object? sender, RoutedEventArgs e)
        {
            OfflineTranslationInstaller.Cancel();
            if (CancelBtn != null) CancelBtn.IsEnabled = false;
        }

        private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnDialogClosed(object? sender, EventArgs e)
        {
            _progressTimer?.Stop();

            // Hủy đăng ký sự kiện tránh rò rỉ bộ nhớ
            OfflineTranslationInstaller.OnLogReceived -= OnInstallerLogReceived;
            OfflineTranslationInstaller.OnProgressChanged -= OnInstallerProgressChanged;
            OfflineTranslationInstaller.OnInstallationCompleted -= OnInstallerCompleted;
        }
    }
}
