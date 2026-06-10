using m_mslc_overlay.core;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace m_mslc_overlay.services;

public class AppContainerHiderService : IDisposable
{
    private IntPtr _targetHwnd = IntPtr.Zero;
    private IntPtr _originalExStyle = IntPtr.Zero;
    private NativeMethods.RECT _originalRect;
    private CancellationTokenSource? _keepAliveCts;
    private bool _wasAlreadyLayered = false;

    public bool IsHidden => _targetHwnd != IntPtr.Zero;
    public uint TargetProcessId { get; private set; }

    public uint PreFindTargetProcessId(string processName = "LiveCaptions")
    {
        Process[] processes = Process.GetProcessesByName(processName);
        if (processes.Length > 0)
        {
            TargetProcessId = (uint)processes[0].Id;
            
            // Định vị cửa sổ chính của tiến trình dựa trên PID và tính hiển thị (locale-safe)
            IntPtr foundHwnd = IntPtr.Zero;
            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hwnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                    if (windowPid == TargetProcessId)
                    {
                        foundHwnd = hwnd;
                        return false; // Dừng duyệt tiếp
                    }
                }
                return true; // Tiếp tục duyệt
            }, IntPtr.Zero);

            _targetHwnd = foundHwnd;
        }

        // Fallback về cách tìm theo tiêu đề tĩnh cũ nếu EnumWindows không tìm thấy
        if (_targetHwnd == IntPtr.Zero) 
            _targetHwnd = NativeMethods.FindWindow(null, "Live captions");
        if (_targetHwnd == IntPtr.Zero) 
            _targetHwnd = NativeMethods.FindWindow(null, "Chú thích trực tiếp");

        if (_targetHwnd != IntPtr.Zero && TargetProcessId == 0)
        {
            NativeMethods.GetWindowThreadProcessId(_targetHwnd, out uint pid);
            TargetProcessId = pid;
        }
        return TargetProcessId;
    }

    public bool HideTargetApp(string processName = "LiveCaptions")
    {
        if (IsHidden) return true; // Đang ẩn sẵn

        if (TargetProcessId == 0 && PreFindTargetProcessId(processName) == 0)
        {
            Debug.WriteLine($"Không tìm thấy Windows Container app mục tiêu mang tên: {processName}");
            return false;
        }

        // 1. Sao lưu cấu hình hiện tại (style và tọa độ màn hình gốc)
        NativeMethods.GetWindowRect(_targetHwnd, out _originalRect);
        _originalExStyle = NativeMethods.GetWindowLongPtrSafety(_targetHwnd, NativeMethods.GWL_EXSTYLE);
        _wasAlreadyLayered = (_originalExStyle.ToInt64() & NativeMethods.WS_EX_LAYERED) != 0;

        // 2. Ép thêm cờ WS_EX_LAYERED nếu ban đầu chưa có để can thiệp bộ đệm Alpha
        if (!_wasAlreadyLayered)
        {
            long newStyle = _originalExStyle.ToInt64() | NativeMethods.WS_EX_LAYERED;
            NativeMethods.SetWindowLongPtrSafety(_targetHwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newStyle));
        }

        // 3. Mức Micro-Opacity (1/255) -> Vẫn render đánh lừa TaskManager nhưng mắt không thấy
        NativeMethods.SetLayeredWindowAttributes(_targetHwnd, 0, 1, NativeMethods.LWA_ALPHA);

        // 4. Di chuyển ra vùng vô định ngoài màn hình tránh cắn nhầm thao tác chuột
        NativeMethods.SetWindowPos(_targetHwnd, IntPtr.Zero, -32000, -32000, 0, 0, 
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 5. Khởi chạy Watchdog Timer bơm tin nhắn trống chặn hệ thống Suspend app
        StartKeepAliveLoop();

        return true;
    }

    public void RestoreTargetApp()
    {
        if (!IsHidden) return;

        // 1. Dừng Watchdog Flooding
        _keepAliveCts?.Cancel();
        _keepAliveCts = null;

        // Thả lỏng CPU (Reset sleep state) gọi tại calling thread
        NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS);

        // 2. Triệu hồi từ tọa độ vô định về tọa độ gốc
        NativeMethods.SetWindowPos(_targetHwnd, IntPtr.Zero, 
            _originalRect.Left, _originalRect.Top, 0, 0, 
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 3. Tái tạo 100% độ đục
        NativeMethods.SetLayeredWindowAttributes(_targetHwnd, 0, 255, NativeMethods.LWA_ALPHA);

        // 4. Hoàn nguyên cấu trúc Style gốc của Microsoft (chỉ xóa WS_EX_LAYERED nếu ban đầu không có)
        if (!_wasAlreadyLayered)
        {
            NativeMethods.SetWindowLongPtrSafety(_targetHwnd, NativeMethods.GWL_EXSTYLE, _originalExStyle);
        }

        _targetHwnd = IntPtr.Zero;
        TargetProcessId = 0;
    }

    private void StartKeepAliveLoop()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts = new CancellationTokenSource();
        var token = _keepAliveCts.Token;

        // Chặn luồng CPU bước vào Sleep-State (Gọi trên Calling Thread trước khi chạy Task)
        NativeMethods.SetThreadExecutionState(
            NativeMethods.EXECUTION_STATE.ES_CONTINUOUS | 
            NativeMethods.EXECUTION_STATE.ES_SYSTEM_REQUIRED | 
            NativeMethods.EXECUTION_STATE.ES_DISPLAY_REQUIRED);

        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (TargetProcessId != 0)
                    {
                        bool isAlive = false;
                        try
                        {
                            using var proc = Process.GetProcessById((int)TargetProcessId);
                            isAlive = !proc.HasExited;
                        }
                        catch
                        {
                            isAlive = false;
                        }

                        if (!isAlive)
                        {
                            // Tiến trình đích đã đóng đột ngột, dọn dẹp tài nguyên và thoát loop
                            Debug.WriteLine("[AppContainerHiderService] Target process terminated. Stopping keep-alive watchdog.");
                            _targetHwnd = IntPtr.Zero;
                            TargetProcessId = 0;
                            break;
                        }
                    }

                    if (_targetHwnd != IntPtr.Zero)
                    {
                        // Ném đá dò đường WM_NULL (Giao tiếp cấp thấp giả mạo giúp qua mặt PLM Watchdog)
                        NativeMethods.SendMessage(_targetHwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                    }
                    await Task.Delay(2000, token); // Chu kỳ định kỳ đập nhịp tim 2s
                }
            }
            catch (TaskCanceledException) {}
        }, token);
    }

    public void Dispose()
    {
        RestoreTargetApp();
    }
}
