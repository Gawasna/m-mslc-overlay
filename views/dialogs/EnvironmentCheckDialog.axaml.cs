using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class EnvironmentCheckDialog : Window
    {
        public EnvironmentCheckDialog()
        {
            InitializeComponent();
            _ = RunCheckAsync();
        }

        private async Task RunCheckAsync()
        {
            var result = await EnvironmentCheckerService.RunDiagnosticAsync();

            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                UpdateUI(result);
            });
        }

        private void UpdateUI(EnvironmentCheckResult res)
        {
            // 1. UAC Badge
            if (UacText != null && UacBadge != null)
            {
                if (res.IsElevated)
                {
                    UacText.Text = "UAC: Administrator (Quyền cao)";
                    UacBadge.Background = Brush.Parse("#ECFDF5");
                    UacBadge.BorderBrush = Brush.Parse("#10B981");
                    UacText.Foreground = Brush.Parse("#047857");
                }
                else
                {
                    UacText.Text = "UAC: Standard User (Thường)";
                    UacBadge.Background = Brush.Parse("#FFFBEB");
                    UacBadge.BorderBrush = Brush.Parse("#F59E0B");
                    UacText.Foreground = Brush.Parse("#B45309");
                }
            }

            // 2. Python Card
            if (PythonVerText != null && PythonPathText != null && PythonStatusBadge != null && PythonStatusText != null)
            {
                if (res.HasPython)
                {
                    PythonVerText.Text = $"Phiên bản: {res.PythonVersion}";
                    PythonPathText.Text = $"Đường dẫn: {res.PythonPath}";
                    PythonStatusText.Text = "PASS";
                    PythonStatusText.Foreground = Brush.Parse("#047857");
                    PythonStatusBadge.Background = Brush.Parse("#ECFDF5");
                    PythonStatusBadge.BorderBrush = Brush.Parse("#10B981");
                }
                else
                {
                    PythonVerText.Text = "Chưa phát hiện Python trong PATH hoặc Registry.";
                    PythonPathText.Text = "Khuyến nghị: Cài đặt Python 3.10+ và tích hợp vào biến môi trường PATH.";
                    PythonStatusText.Text = "FAIL";
                    PythonStatusText.Foreground = Brush.Parse("#DC2626");
                    PythonStatusBadge.Background = Brush.Parse("#FEF2F2");
                    PythonStatusBadge.BorderBrush = Brush.Parse("#EF4444");
                }
            }

            // 3. Pip Card
            if (PipVerText != null && PipStatusBadge != null && PipStatusText != null)
            {
                if (res.HasPip)
                {
                    PipVerText.Text = $"Phiên bản: {res.PipVersion}";
                    PipStatusText.Text = "PASS";
                    PipStatusText.Foreground = Brush.Parse("#047857");
                    PipStatusBadge.Background = Brush.Parse("#ECFDF5");
                    PipStatusBadge.BorderBrush = Brush.Parse("#10B981");
                }
                else
                {
                    PipVerText.Text = "Chưa cài đặt pip hoặc chưa tích hợp vào PATH.";
                    PipStatusText.Text = "FAIL";
                    PipStatusText.Foreground = Brush.Parse("#DC2626");
                    PipStatusBadge.Background = Brush.Parse("#FEF2F2");
                    PipStatusBadge.BorderBrush = Brush.Parse("#EF4444");
                }
            }

            // 4. CPU Card
            if (CpuModelText != null && CpuClockText != null && CpuLoadText != null)
            {
                CpuModelText.Text = $"Model: {res.CpuName}";
                CpuClockText.Text = $"Xung nhịp: {res.CpuClockSpeed}";
                CpuLoadText.Text = res.CpuAverageLoad >= 0 ? $"Tải trung bình: {res.CpuAverageLoad:F1}%" : "Tải trung bình: N/A";
            }

            // 5. GPU & CUDA Card
            if (GpuNameText != null && CudaStatusText != null && CudaStatusBadge != null && CudaBadgeText != null)
            {
                GpuNameText.Text = $"GPU: {(string.IsNullOrEmpty(res.GpuName) ? "Card màn hình tiêu chuẩn" : res.GpuName)}";
                CudaStatusText.Text = $"CUDA Support: {(res.HasCuda ? res.CudaVersion : "Không phát hiện CUDA (Chạy chế độ CPU)")}";

                if (res.HasCuda)
                {
                    CudaBadgeText.Text = "CUDA READY";
                    CudaBadgeText.Foreground = Brush.Parse("#047857");
                    CudaStatusBadge.Background = Brush.Parse("#ECFDF5");
                    CudaStatusBadge.BorderBrush = Brush.Parse("#10B981");
                }
                else
                {
                    CudaBadgeText.Text = "CPU MODE";
                    CudaBadgeText.Foreground = Brush.Parse("#4B5563");
                    CudaStatusBadge.Background = Brush.Parse("#F3F4F6");
                    CudaStatusBadge.BorderBrush = Brush.Parse("#D1D5DB");
                }
            }

            // 6. LiveCaptions.exe Card
            if (LiveCaptionsStateText != null && LiveCaptionsPathText != null && LiveCaptionsBadge != null && LiveCaptionsBadgeText != null)
            {
                if (res.HasLiveCaptionsBinary)
                {
                    LiveCaptionsStateText.Text = res.IsLiveCaptionsRunning 
                        ? $"Trạng thái: ĐANG CHẠY (PID: {res.LiveCaptionsPid})" 
                        : "Trạng thái: Đã có binary (Chưa khởi chạy)";
                    LiveCaptionsPathText.Text = $"Đường dẫn: {(string.IsNullOrEmpty(res.LiveCaptionsPath) ? "Windows System Package" : res.LiveCaptionsPath)}";
                    
                    LiveCaptionsBadgeText.Text = "FOUND";
                    LiveCaptionsBadgeText.Foreground = Brush.Parse("#047857");
                    LiveCaptionsBadge.Background = Brush.Parse("#ECFDF5");
                    LiveCaptionsBadge.BorderBrush = Brush.Parse("#10B981");
                }
                else
                {
                    LiveCaptionsStateText.Text = "Trạng thái: Không tìm thấy binary LiveCaptions.exe";
                    LiveCaptionsPathText.Text = "Hướng dẫn: Vui lòng bật tính năng Live Captions trong Windows 11 Settings (Accessibility -> Captions).";
                    
                    LiveCaptionsBadgeText.Text = "NOT FOUND";
                    LiveCaptionsBadgeText.Foreground = Brush.Parse("#DC2626");
                    LiveCaptionsBadge.Background = Brush.Parse("#FEF2F2");
                    LiveCaptionsBadge.BorderBrush = Brush.Parse("#EF4444");
                }
            }

            // 7. Raw Log Output
            if (RawLogBox != null)
            {
                RawLogBox.Text = res.RawSummaryLog;
            }
        }

        private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        public static async Task ShowDiagnosticAsync(Window owner)
        {
            var dialog = new EnvironmentCheckDialog();
            await dialog.ShowDialog(owner);
        }
    }
}
