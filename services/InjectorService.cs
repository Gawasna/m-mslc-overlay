using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    public class InjectorService
    {
        public async Task<bool> InjectAsync(uint pid)
        {
            try
            {
                // Ưu tiên dò tìm vị trí thực thi của thư viện Host.exe trong thư mục extractor
                string hostPath = Path.Combine(AppContext.BaseDirectory, "extractor", "Host.exe");
                
                if (!File.Exists(hostPath))
                {
                    // Fallback 1: Thư mục BaseDirectory trực tiếp
                    hostPath = Path.Combine(AppContext.BaseDirectory, "Host.exe");
                }

                if (!File.Exists(hostPath))
                {
                    // Fallback 2: Thư mục cha (phát triển cục bộ)
                    string rootDir = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.FullName ?? AppContext.BaseDirectory;
                    hostPath = Path.Combine(rootDir, "Host.exe");
                }

                if (!File.Exists(hostPath))
                {
                    Debug.WriteLine($"Trình Injector (Host.exe) không tồn tại tại đường dẫn: {hostPath}");
                    return false;
                }

                var process = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = hostPath,
                        Arguments = $"--pid {pid} --inject-only",
                        UseShellExecute = true,
                        Verb = "runas", // UAC Prompt bắt buộc để Inject cấp hệ thống
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                // Đợi quá trình Inject (thực tế chạy rất nhanh) xả Thread
                await process.WaitForExitAsync();

                // 0 là Exit Code chuẩn do C++ Loader trả về khi đính kèm HookCore.dll thành công
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InjectorService] Lỗi phát sinh trong quá trình nạp DLL: {ex.Message}");
                return false;
            }
        }
    }
}
