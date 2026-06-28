using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Text;
using System.Threading.Tasks;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class ExtractorUpdateDialog : Window
    {
        private GitHubRelease? _latestRelease;
        private readonly StringBuilder _logs = new StringBuilder();
        
        private double _targetProgress = 0;
        private readonly Avalonia.Threading.DispatcherTimer? _progressTimer;

        public ExtractorUpdateDialog()
        {
            InitializeComponent();
            
            // Hiển thị phiên bản hiện tại
            string currentTag = ConfigManager.Current.ExtractorTag;
            LocalVersionText.Text = string.IsNullOrEmpty(currentTag) ? "Chưa cài đặt" : currentTag;

            // Đăng ký sự kiện từ service
            ExtractorUpdateService.OnLogReceived += OnUpdateLogReceived;
            ExtractorUpdateService.OnProgressChanged += OnUpdateProgressChanged;
            ExtractorUpdateService.OnUpdateCompleted += OnUpdateCompleted;

            this.Closed += OnDialogClosed;

            // Khởi chạy timer cập nhật ProgressBar mượt mà
            _progressTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(20) // ~50 FPS
            };
            _progressTimer.Tick += OnProgressTimerTick;
            _progressTimer.Start();

            // Tự động kiểm tra cập nhật khi mở Dialog
            _ = AutoCheckUpdateAsync();
        }

        private async Task AutoCheckUpdateAsync()
        {
            await CheckUpdateInternalAsync();
        }

        private void OnProgressTimerTick(object? sender, EventArgs e)
        {
            if (ProgressBar == null) return;

            double current = ProgressBar.Value;
            if (current < _targetProgress)
            {
                double diff = _targetProgress - current;
                double step = Math.Max(0.5, diff * 0.1);
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

        private void OnUpdateLogReceived(string logLine)
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

        private void OnUpdateProgressChanged(double val)
        {
            _targetProgress = val;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (StatusText != null)
                {
                    if (val < 10)
                    {
                        StatusText.Text = "Khởi tạo kết nối...";
                    }
                    else if (val < 80)
                    {
                        StatusText.Text = $"Đang tải xuống bộ nhị phân: {val:0.0}%";
                    }
                    else if (val < 90)
                    {
                        StatusText.Text = "Đang giải nén bộ cài đặt...";
                    }
                    else if (val < 95)
                    {
                        StatusText.Text = "Đang kiểm tra tính toàn vẹn (SHA256)...";
                    }
                    else if (val < 100)
                    {
                        StatusText.Text = "Đang ánh xạ và ghi đè tệp tin...";
                    }
                    else
                    {
                        StatusText.Text = "Hoàn tất cập nhật.";
                    }
                }
            });
        }

        private void OnUpdateCompleted(bool success, string msg)
        {
            _progressTimer?.Stop();

            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (ProgressBar != null)
                {
                    ProgressBar.Value = success ? 100 : _targetProgress;
                }

                if (CancelBtn != null) CancelBtn.IsEnabled = false;
                if (CloseBtn != null) CloseBtn.IsEnabled = true;
                if (CheckBtn != null) CheckBtn.IsEnabled = true;
                if (UpdateBtn != null) UpdateBtn.IsEnabled = !success && _latestRelease != null;

                if (StatusText != null)
                {
                    StatusText.Text = success ? "Cập nhật thành công!" : $"Cập nhật thất bại: {msg}";
                }

                if (success)
                {
                    UpdateStepText("[✔]", "Active Extractor đã được cập nhật thành công!", Avalonia.Media.Brushes.Green);
                    // Cập nhật lại UI hiển thị phiên bản hiện tại
                    string currentTag = ConfigManager.Current.ExtractorTag;
                    LocalVersionText.Text = string.IsNullOrEmpty(currentTag) ? "Chưa cài đặt" : currentTag;
                }
                else
                {
                    UpdateStepText("[✘]", $"Lỗi: {msg}", Avalonia.Media.Brushes.Red);
                }
            });
        }

        private async void CheckBtn_Click(object? sender, RoutedEventArgs e)
        {
            await CheckUpdateInternalAsync();
        }

        private async Task CheckUpdateInternalAsync()
        {
            if (CheckBtn != null) CheckBtn.IsEnabled = false;
            if (UpdateBtn != null) UpdateBtn.IsEnabled = false;
            
            UpdateStepText("[▶]", "Đang kết nối GitHub API để kiểm tra phiên bản mới...", Avalonia.Media.Brushes.DarkOrange);
            _latestRelease = await ExtractorUpdateService.CheckForUpdateAsync();

            if (_latestRelease == null)
            {
                LatestVersionText.Text = "Không thể lấy thông tin";
                LatestVersionText.Foreground = Avalonia.Media.Brushes.Red;
                UpdateStepText("[✘]", "Lỗi kết nối GitHub API hoặc không tìm thấy release.", Avalonia.Media.Brushes.Red);
                if (CheckBtn != null) CheckBtn.IsEnabled = true;
                return;
            }

            LatestVersionText.Text = _latestRelease.TagName;
            LatestVersionText.Foreground = Avalonia.Media.Brushes.Green;

            string localTag = ConfigManager.Current.ExtractorTag;
            bool hasUpdate = string.IsNullOrEmpty(localTag) || localTag != _latestRelease.TagName;

            if (hasUpdate)
            {
                UpdateStepText("[✔]", "Có phiên bản mới khả dụng. Sẵn sàng cập nhật.", Avalonia.Media.Brushes.Green);
                if (UpdateBtn != null) UpdateBtn.IsEnabled = true;
            }
            else
            {
                UpdateStepText("[✔]", "Active Extractor hiện tại đang ở phiên bản mới nhất.", Avalonia.Media.Brushes.Green);
            }

            if (CheckBtn != null) CheckBtn.IsEnabled = true;
        }

        private async void UpdateBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_latestRelease == null) return;

            if (CheckBtn != null) CheckBtn.IsEnabled = false;
            if (UpdateBtn != null) UpdateBtn.IsEnabled = false;
            if (CloseBtn != null) CloseBtn.IsEnabled = false;
            if (CancelBtn != null) CancelBtn.IsEnabled = true;
            if (ProgressBar != null) ProgressBar.IsEnabled = true;

            _logs.Clear();
            if (LogBox != null) LogBox.Text = "";

            UpdateStepText("[▶]", "Đang tải bản cập nhật...", Avalonia.Media.Brushes.DarkOrange);
            
            // Chạy bất đồng bộ
            await Task.Run(() => ExtractorUpdateService.RunUpdateAsync(_latestRelease));
        }

        private void CancelBtn_Click(object? sender, RoutedEventArgs e)
        {
            ExtractorUpdateService.Cancel();
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
            ExtractorUpdateService.OnLogReceived -= OnUpdateLogReceived;
            ExtractorUpdateService.OnProgressChanged -= OnUpdateProgressChanged;
            ExtractorUpdateService.OnUpdateCompleted -= OnUpdateCompleted;
        }
    }
}
