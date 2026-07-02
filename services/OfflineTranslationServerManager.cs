using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    public enum OfflineServerState
    {
        Stopped,
        Starting,
        Ready,
        ModelMissing,
        Failed
    }

    public static class OfflineTranslationServerManager
    {
        private static Process? _serverProcess;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        private static readonly string LogTag = "[OfflineTranslationServerManager]";
        
        public static OfflineServerState State { get; private set; } = OfflineServerState.Stopped;
        public static string LastErrorMessage { get; private set; } = string.Empty;
        public static string DetectedServerDir { get; private set; } = string.Empty;
        public static int ServerPort { get; set; } = 11435;

        // Sự kiện thông báo khi trạng thái thay đổi
        public static event Action<OfflineServerState>? OnStateChanged;

        /// <summary>
        /// Dò tìm và kiểm tra thư mục chứa server dịch thuật offline dựa trên cấu hình
        /// </summary>
        // Cache để tránh log spam khi FindServerDirectory() được gọi liên tục từ polling loop
        private static string _lastLoggedServerDir = string.Empty;

        public static string FindServerDirectory()
        {
            string configuredPath = ConfigManager.Current.OfflineServerDir;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = "plugins/atom26";
            }

            string targetPath;
            if (Path.IsPathRooted(configuredPath))
            {
                targetPath = configuredPath;
            }
            else
            {
                targetPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));
            }

            // Log chỉ khi path thay đổi — tránh spam từ polling loop gọi liên tục
            if (targetPath != _lastLoggedServerDir)
            {
                _lastLoggedServerDir = targetPath;
                LoggerService.Log($"{LogTag} Target server directory resolved to: {targetPath}");
            }

            if (IsValidServerDirectory(targetPath))
            {
                return targetPath;
            }

            LoggerService.Log($"{LogTag} Path '{targetPath}' is not a valid offline server directory.");
            return string.Empty;
        }

        private static bool IsValidServerDirectory(string path)
        {
            if (!Directory.Exists(path)) return false;

            // Kiểm tra sự tồn tại của script chính và venv
            string scriptPath = Path.Combine(path, "translation_server.py");
            string venvPython = Path.Combine(path, "venv", "Scripts", "python.exe");
            string compiledExe = Path.Combine(path, "translation_server.exe");

            return File.Exists(compiledExe) || (File.Exists(scriptPath) && File.Exists(venvPython));
        }

        private static void UpdateState(OfflineServerState newState, string errorMsg = "")
        {
            if (State != newState)
            {
                State = newState;
                LastErrorMessage = errorMsg;
                LoggerService.Log($"{LogTag} State changed to: {newState}.{(string.IsNullOrEmpty(errorMsg) ? "" : " Error: " + errorMsg)}");
                OnStateChanged?.Invoke(newState);
            }
        }

        /// <summary>
        /// Khởi chạy Offline Server bất đồng bộ
        /// </summary>
        public static async Task<bool> StartServerAsync()
        {
            if (State == OfflineServerState.Ready || State == OfflineServerState.Starting)
            {
                // Kiểm tra xem server thực tế có đang phản hồi hay không
                bool isAlive = await PingServerAsync();
                if (isAlive)
                {
                    UpdateState(OfflineServerState.Ready);
                    return true;
                }
            }

            UpdateState(OfflineServerState.Starting);

            string serverDir = FindServerDirectory();
            if (string.IsNullOrEmpty(serverDir))
            {
                UpdateState(OfflineServerState.Failed, "Không tìm thấy thư mục cài đặt Offline Translation Server.");
                return false;
            }

            DetectedServerDir = serverDir;

            string scriptPath = Path.Combine(serverDir, "translation_server.py");
            string venvPython = Path.Combine(serverDir, "venv", "Scripts", "python.exe");
            string compiledExe = Path.Combine(serverDir, "translation_server.exe");

            string execPath = "";
            string arguments = "";

            if (File.Exists(compiledExe))
            {
                execPath = compiledExe;
                arguments = "";
                LoggerService.Log($"{LogTag} Launching compiled server executable: {execPath}");
            }
            else if (File.Exists(scriptPath) && File.Exists(venvPython))
            {
                execPath = venvPython;
                arguments = $"\"{scriptPath}\"";
                LoggerService.Log($"{LogTag} Launching server script via venv: {execPath} {arguments}");
            }
            else
            {
                UpdateState(OfflineServerState.Failed, "Thiếu file python server hoặc venv.");
                return false;
            }

            try
            {
                // Dọn dẹp tiến trình cũ nếu còn sót
                StopServer();

                var startInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = arguments,
                    WorkingDirectory = serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // Thiết lập port qua biến môi trường
                startInfo.EnvironmentVariables["PORT"] = ServerPort.ToString();

                // Thiết lập đường dẫn mô hình được chọn qua biến môi trường MODEL_PATH
                string selectedModel = ConfigManager.Current.OfflineModel;
                string modelSubDir = selectedModel == "OPUS-MT" ? "models/opus-en-vi-int8" : "models/nllb-600m-int8";
                startInfo.EnvironmentVariables["MODEL_PATH"] = modelSubDir;

                _serverProcess = new Process { StartInfo = startInfo };
                _serverProcess.OutputDataReceived += (s, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LoggerService.Log($"[OfflineServer-Out] {e.Data.Trim()}");
                    }
                };
                _serverProcess.ErrorDataReceived += (s, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LoggerService.Log($"[OfflineServer-Err] {e.Data.Trim()}");
                    }
                };

                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();

                LoggerService.Log($"{LogTag} Process started successfully. PID: {_serverProcess.Id}");

                // Chờ đợi server khởi chạy thành công (ping kiểm tra)
                int maxRetries = 15;
                for (int i = 0; i < maxRetries; i++)
                {
                    await Task.Delay(1000);
                    if (_serverProcess == null || _serverProcess.HasExited)
                    {
                        UpdateState(OfflineServerState.Failed, "Tiến trình server kết thúc đột ngột ngay sau khi khởi chạy.");
                        return false;
                    }

                    if (await PingServerAsync())
                    {
                        LoggerService.Log($"{LogTag} Server responded. Fetching engine status...");
                        await CheckEngineStatusAsync();
                        return true;
                    }
                }

                UpdateState(OfflineServerState.Failed, "Server không phản hồi sau thời gian chờ tối đa.");
                return false;
            }
            catch (Exception ex)
            {
                UpdateState(OfflineServerState.Failed, $"Lỗi khởi chạy tiến trình: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Dừng Offline Server
        /// </summary>
        public static void StopServer()
        {
            try
            {
                if (_serverProcess != null && !_serverProcess.HasExited)
                {
                    LoggerService.Log($"{LogTag} Stopping offline server process (PID: {_serverProcess.Id})...");
                    _serverProcess.Kill(true);
                    _serverProcess.Dispose();
                }
            }
            catch (Exception ex)
            {
                LoggerService.Log($"{LogTag} Error killing server process: {ex.Message}");
            }
            finally
            {
                _serverProcess = null;
                UpdateState(OfflineServerState.Stopped);
            }
        }

        private static async Task<bool> PingServerAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"http://127.0.0.1:{ServerPort}/status");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static async Task CheckEngineStatusAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"http://127.0.0.1:{ServerPort}/status");
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("status", out var statusProp))
                    {
                        string status = statusProp.GetString() ?? "";
                        if (status == "ready")
                        {
                            UpdateState(OfflineServerState.Ready);
                        }
                        else if (status == "model_missing")
                        {
                            UpdateState(OfflineServerState.ModelMissing, "Thiếu dữ liệu mô hình trong thư mục models/. Hãy tải mô hình trước.");
                        }
                        else
                        {
                            UpdateState(OfflineServerState.Failed, $"Trạng thái engine không xác định: {status}");
                        }
                    }
                }
                else
                {
                    UpdateState(OfflineServerState.Failed, $"Server trả về lỗi: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                UpdateState(OfflineServerState.Failed, $"Lỗi phân tích trạng thái server: {ex.Message}");
            }
        }
    }
}
