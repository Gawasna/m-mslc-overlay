using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    public static class OfflineTranslationInstaller
    {
        private static Process? _currentProcess;
        private static bool _cancelRequested = false;
        private static readonly string LogTag = "[OfflineTranslationInstaller]";

        public static bool IsInstalling { get; private set; } = false;

        public static event Action<string>? OnLogReceived;
        public static event Action<double>? OnProgressChanged; // 0.0 to 100.0
        public static event Action<bool, string>? OnInstallationCompleted; // (success, message)

        public static void Cancel()
        {
            if (IsInstalling)
            {
                _cancelRequested = true;
                Log("Yêu cầu hủy cài đặt từ người dùng...");
                try
                {
                    if (_currentProcess != null && !_currentProcess.HasExited)
                    {
                        _currentProcess.Kill(true);
                        _currentProcess.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log($"Lỗi khi dừng tiến trình: {ex.Message}");
                }
            }
        }

        private static void Log(string message)
        {
            LoggerService.Log($"{LogTag} {message}");
            OnLogReceived?.Invoke(message);
        }

        private static void SetProgress(double progress)
        {
            OnProgressChanged?.Invoke(progress);
        }

        /// <summary>
        /// Thực hiện quy trình cài đặt Offline Translation Server bất đồng bộ
        /// </summary>
        /// <param name="modelId">ID mô hình HuggingFace (ví dụ: facebook/nllb-200-distilled-600m hoặc Helsinki-NLP/opus-mt-en-vi)</param>
        /// <param name="modelOutputDir">Thư mục đầu ra của mô hình (ví dụ: models/nllb-600m-int8 hoặc models/opus-en-vi-int8)</param>
        public static async Task StartInstallAsync(string modelId, string modelOutputDir)
        {
            if (IsInstalling)
            {
                OnInstallationCompleted?.Invoke(false, "Quá trình cài đặt đang diễn ra.");
                return;
            }

            IsInstalling = true;
            _cancelRequested = false;
            SetProgress(0);

            try
            {
                string targetDir = OfflineTranslationServerManager.FindServerDirectory();
                if (string.IsNullOrEmpty(targetDir))
                {
                    // Nếu thư mục target chưa hợp lệ (ví dụ: chưa có file script nào), 
                    // ta sẽ dùng thư mục cấu hình mặc định là AppDomain.BaseDirectory + "plugins/atom26"
                    string configuredPath = ConfigManager.Current.OfflineServerDir;
                    if (string.IsNullOrWhiteSpace(configuredPath))
                    {
                        configuredPath = "plugins/atom26";
                    }

                    if (Path.IsPathRooted(configuredPath))
                    {
                        targetDir = configuredPath;
                    }
                    else
                    {
                        targetDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));
                    }
                }

                Log($"Thư mục cài đặt đích: {targetDir}");
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    Log("Đã tạo thư mục đích.");
                }

                // Bước 1: Kiểm tra Python hệ thống
                SetProgress(10);
                Log("=== [Bước 1/5] Kiểm tra Python hệ thống ===");
                bool hasPython = await RunCommandAsync("python", "--version", targetDir);
                if (!hasPython)
                {
                    Log("Lỗi: Không tìm thấy Python trong hệ thống. Vui lòng cài đặt Python (phiên bản >= 3.9) và tích hợp vào biến môi trường PATH.");
                    OnInstallationCompleted?.Invoke(false, "Không tìm thấy Python. Vui lòng cài đặt Python trước.");
                    return;
                }

                if (_cancelRequested) { HandleCancel(); return; }

                // Bước 2: Đảm bảo source script được cài đặt qua manifest (Pattern B)
                // Dev-mode: source_url=local:plugins/atom26 → copy từ repo root
                // Production: source_url=https://... → download ZIP, verify SHA256, extract
                SetProgress(20);
                Log("=== [Bước 2/5] Chuẩn bị tệp tin script (via Plugin Manifest) ===");
                bool scriptsReady = await PluginManifestService.EnsureInstalledAsync(
                    "atom26",
                    onLog: Log,
                    onProgress: pct => SetProgress(20.0 + pct * 0.08));

                if (!scriptsReady)
                {
                    Log("Lỗi: Không thể chuẩn bị các tệp script từ plugin manifest.");
                    OnInstallationCompleted?.Invoke(false, "Không thể tải/chuẩn bị plugin atom26 từ manifest.");
                    return;
                }

                if (_cancelRequested) { HandleCancel(); return; }

                // Bước 3: Khởi tạo Virtual Environment
                SetProgress(30);
                Log("=== [Bước 3/5] Khởi tạo Virtual Environment (venv) ===");
                Log("Đang chạy 'python -m venv venv'... Việc này có thể mất 10-30 giây.");
                bool venvSuccess = await RunCommandAsync("python", "-m venv venv", targetDir);
                if (!venvSuccess)
                {
                    Log("Lỗi: Khởi tạo venv thất bại.");
                    OnInstallationCompleted?.Invoke(false, "Tạo venv thất bại.");
                    return;
                }

                string venvPython = Path.Combine(targetDir, "venv", "Scripts", "python.exe");
                string venvPip = Path.Combine(targetDir, "venv", "Scripts", "pip.exe");

                if (!File.Exists(venvPython) || !File.Exists(venvPip))
                {
                    Log("Lỗi: Không tìm thấy file python/pip thực thi trong venv sau khi tạo.");
                    OnInstallationCompleted?.Invoke(false, "Không tìm thấy python/pip trong venv.");
                    return;
                }

                if (_cancelRequested) { HandleCancel(); return; }

                // Bước 4: Cài đặt các thư viện phụ thuộc (dependencies)
                SetProgress(50);
                Log("=== [Bước 4/5] Cài đặt các thư viện phụ thuộc (pip install) ===");
                Log("Đang chạy 'pip install --no-cache-dir -r requirements.txt'... Việc này có thể mất từ 1-3 phút tùy thuộc tốc độ mạng.");
                
                // Nâng cấp pip trước để tránh lỗi tương thích
                await RunCommandAsync(venvPython, "-m pip install --no-cache-dir --upgrade pip", targetDir);
                
                bool pipSuccess = await RunCommandAsync(venvPip, "install --no-cache-dir -r requirements.txt", targetDir);
                if (!pipSuccess)
                {
                    Log("Lỗi: Cài đặt dependencies thất bại.");
                    OnInstallationCompleted?.Invoke(false, "Cài đặt dependencies từ requirements.txt thất bại.");
                    return;
                }

                if (_cancelRequested) { HandleCancel(); return; }

                // Bước 5: Tải và chuyển đổi mô hình
                SetProgress(75);
                Log("=== [Bước 5/5] Tải và Lượng tử hóa mô hình ===");
                Log($"Đang chạy 'python model_downloader.py --model {modelId} --output {modelOutputDir}'...");
                Log("Mô hình sẽ được tự động tải từ Hugging Face Hub và lượng tử hóa sang định dạng CTranslate2 INT8 để tiết kiệm RAM/VRAM.");
                
                bool downloadSuccess = await RunCommandAsync(venvPython, $"model_downloader.py --model \"{modelId}\" --output \"{modelOutputDir}\"", targetDir);
                if (!downloadSuccess)
                {
                    Log("Lỗi: Tải và convert mô hình thất bại.");
                    OnInstallationCompleted?.Invoke(false, "Tải và lượng tử hóa mô hình từ Hugging Face thất bại.");
                    return;
                }

                SetProgress(100);
                Log("=== CÀI ĐẶT THÀNH CÔNG ===");
                Log("Plugin Offline Translation đã được cấu hình thành công!");
                Log("Bây giờ bạn đã có thể khởi chạy server dịch offline trực tiếp trên app.");
                
                // Chạy job ngầm dọn dẹp cache
                CleanTempFolders(targetDir);

                OnInstallationCompleted?.Invoke(true, "Cài đặt plugin thành công!");
            }
            catch (Exception ex)
            {
                Log($"Lỗi nghiêm trọng trong quá trình cài đặt: {ex.Message}");
                OnInstallationCompleted?.Invoke(false, $"Lỗi: {ex.Message}");
            }
            finally
            {
                IsInstalling = false;
                _currentProcess = null;
            }
        }

        private static void CleanTempFolders(string targetDir)
        {
            Task.Run(() => {
                try
                {
                    Log("Đang chạy job ngầm dọn dẹp cache và các tệp cài đặt tạm thời...");

                    // 1. Dọn dẹp thư mục temp_hf_cache nếu còn sót
                    string tempHfCache = Path.Combine(targetDir, "models", "temp_hf_cache");
                    if (Directory.Exists(tempHfCache))
                    {
                        Directory.Delete(tempHfCache, true);
                        Log("Đã xóa thư mục cache Hugging Face tạm thời.");
                    }
                    
                    // 2. Thực hiện dọn dẹp pip cache của python venv (nếu có)
                    string venvPip = Path.Combine(targetDir, "venv", "Scripts", "pip.exe");
                    if (File.Exists(venvPip))
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = venvPip,
                            Arguments = "cache purge",
                            WorkingDirectory = targetDir,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var p = Process.Start(startInfo))
                        {
                            p?.WaitForExit();
                        }
                        Log("Đã dọn dẹp thành công pip cache.");
                    }
                    
                    Log("Hoàn tất dọn dẹp cache cài đặt.");
                }
                catch (Exception ex)
                {
                    LoggerService.Log($"{LogTag} Lỗi khi chạy job dọn dẹp cache ngầm: {ex.Message}");
                }
            });
        }

        private static void HandleCancel()
        {
            Log("=== ĐÃ HỦY CÀI ĐẶT ===");
            Log("Quá trình cài đặt đã bị người dùng hủy bỏ.");
            OnInstallationCompleted?.Invoke(false, "Đã hủy cài đặt.");
        }

        // PrepareScriptsAsync removed — replaced by PluginManifestService.EnsureInstalledAsync (Pattern B).

        private static async Task<bool> RunCommandAsync(string fileName, string arguments, string workingDir)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _currentProcess = new Process { StartInfo = startInfo };
                
                _currentProcess.OutputDataReceived += (s, e) => {
                    if (e.Data != null) Log(e.Data);
                };
                
                _currentProcess.ErrorDataReceived += (s, e) => {
                    if (e.Data != null) Log($"[ERROR] {e.Data}");
                };

                _currentProcess.Start();
                _currentProcess.BeginOutputReadLine();
                _currentProcess.BeginErrorReadLine();

                await _currentProcess.WaitForExitAsync();
                
                bool success = _currentProcess.ExitCode == 0;
                _currentProcess.Dispose();
                _currentProcess = null;

                return success;
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi chạy lệnh '{fileName} {arguments}': {ex.Message}");
                return false;
            }
        }

        public enum UpdateCheckResult
        {
            UpToDate,
            UpdateAvailable,
            UpdateRequired,
            Error
        }

        public static async Task<UpdateCheckResult> CheckForModelUpdateAsync(string modelId, string modelOutputDir)
        {
            string targetDir = OfflineTranslationServerManager.FindServerDirectory();
            if (string.IsNullOrEmpty(targetDir))
            {
                string configuredPath = ConfigManager.Current.OfflineServerDir;
                targetDir = Path.IsPathRooted(configuredPath) 
                    ? configuredPath 
                    : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));
            }

            string venvPython = Path.Combine(targetDir, "venv", "Scripts", "python.exe");
            string downloaderScript = Path.Combine(targetDir, "model_downloader.py");

            if (!File.Exists(venvPython) || !File.Exists(downloaderScript))
            {
                return UpdateCheckResult.UpdateRequired;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = venvPython,
                    Arguments = $"model_downloader.py --model \"{modelId}\" --output \"{modelOutputDir}\" --check-only",
                    WorkingDirectory = targetDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    var outputBuilder = new StringBuilder();
                    process.OutputDataReceived += (s, e) => {
                        if (e.Data != null) outputBuilder.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    await process.WaitForExitAsync();

                    string output = outputBuilder.ToString();
                    LoggerService.Log($"[OfflineTranslationInstaller] Check update output: {output.Trim()}");

                    if (output.Contains("RESULT: UP_TO_DATE"))
                    {
                        return UpdateCheckResult.UpToDate;
                    }
                    if (output.Contains("RESULT: UPDATE_AVAILABLE"))
                    {
                        return UpdateCheckResult.UpdateAvailable;
                    }
                    if (output.Contains("RESULT: UPDATE_REQUIRED"))
                    {
                        return UpdateCheckResult.UpdateRequired;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[OfflineTranslationInstaller] Error checking update: {ex.Message}");
            }

            return UpdateCheckResult.Error;
        }
    }
}
