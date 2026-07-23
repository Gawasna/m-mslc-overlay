using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace m_mslc_overlay.services
{
    public class EnvironmentCheckResult
    {
        public bool IsElevated { get; set; }
        public string UacStatus { get; set; } = string.Empty;

        // Python
        public bool HasPython { get; set; }
        public string PythonVersion { get; set; } = string.Empty;
        public string PythonPath { get; set; } = string.Empty;

        // Pip
        public bool HasPip { get; set; }
        public string PipVersion { get; set; } = string.Empty;

        // CPU
        public string CpuName { get; set; } = string.Empty;
        public string CpuClockSpeed { get; set; } = string.Empty;
        public float CpuAverageLoad { get; set; }

        // GPU
        public string GpuName { get; set; } = string.Empty;
        public string GpuMemory { get; set; } = string.Empty;
        public bool HasCuda { get; set; }
        public string CudaVersion { get; set; } = string.Empty;

        // LiveCaptions.exe
        public bool HasLiveCaptionsBinary { get; set; }
        public string LiveCaptionsPath { get; set; } = string.Empty;
        public bool IsLiveCaptionsRunning { get; set; }
        public int LiveCaptionsPid { get; set; }

        public string RawSummaryLog { get; set; } = string.Empty;
    }

    public static class EnvironmentCheckerService
    {
        private static readonly string LogTag = "[EnvironmentCheckerService]";

        public static bool IsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static async Task<EnvironmentCheckResult> RunDiagnosticAsync(bool requestElevatedIfPossible = false)
        {
            var result = new EnvironmentCheckResult();
            var sbLog = new StringBuilder();

            sbLog.AppendLine($"=== SYSTEM ENVIRONMENT DIAGNOSTIC REPORT ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");

            // 1. UAC / Admin Status Check
            result.IsElevated = IsAdmin();
            result.UacStatus = result.IsElevated ? "Administrator (Elevated)" : "Standard User (Non-Elevated)";
            sbLog.AppendLine($"[UAC Privilege] {result.UacStatus}");

            // 2. Python Check
            sbLog.AppendLine("\n--- [1/5] Python Environment ---");
            await CheckPythonAsync(result, sbLog);

            // 3. Pip Check
            sbLog.AppendLine("\n--- [2/5] Pip Package Manager ---");
            await CheckPipAsync(result, sbLog);

            // 4. CPU Info Check
            sbLog.AppendLine("\n--- [3/5] CPU Specs & Load ---");
            await CheckCpuAsync(result, sbLog);

            // 5. GPU & CUDA Check
            sbLog.AppendLine("\n--- [4/5] GPU & CUDA Status ---");
            await CheckGpuAndCudaAsync(result, sbLog);

            // 6. LiveCaptions.exe Binary Check
            sbLog.AppendLine("\n--- [5/5] LiveCaptions.exe Binary ---");
            await CheckLiveCaptionsBinaryAsync(result, sbLog);

            sbLog.AppendLine("=================================================");
            result.RawSummaryLog = sbLog.ToString();

            // Record to system log
            LoggerService.Log($"{LogTag} Diagnostic completed:\n{result.RawSummaryLog}");

            return result;
        }

        private static async Task CheckPythonAsync(EnvironmentCheckResult result, StringBuilder sb)
        {
            try
            {
                var (success, output) = await RunCmdAsync("python", "--version");
                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    result.HasPython = true;
                    result.PythonVersion = output.Trim();

                    var (pathOk, pathOut) = await RunCmdAsync("python", "-c \"import sys; print(sys.executable)\"");
                    if (pathOk && !string.IsNullOrWhiteSpace(pathOut))
                    {
                        result.PythonPath = pathOut.Trim();
                    }
                }
                else
                {
                    // Try fallback using py launcher
                    var (pyOk, pyOut) = await RunCmdAsync("py", "-3 --version");
                    if (pyOk && !string.IsNullOrWhiteSpace(pyOut))
                    {
                        result.HasPython = true;
                        result.PythonVersion = pyOut.Trim();
                        var (pathOk, pathOut) = await RunCmdAsync("py", "-3 -c \"import sys; print(sys.executable)\"");
                        if (pathOk) result.PythonPath = pathOut.Trim();
                    }
                }

                if (!result.HasPython)
                {
                    // Check registry
                    string regPython = CheckPythonRegistry();
                    if (!string.IsNullOrEmpty(regPython))
                    {
                        result.HasPython = true;
                        result.PythonVersion = "Python (Found in Registry)";
                        result.PythonPath = regPython;
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[Python Error] {ex.Message}");
            }

            if (result.HasPython)
            {
                sb.AppendLine($"Status: PASS | Version: {result.PythonVersion}");
                sb.AppendLine($"Executable Path: {result.PythonPath}");
            }
            else
            {
                sb.AppendLine("Status: FAIL | Python not detected in PATH or Registry.");
            }
        }

        private static string CheckPythonRegistry()
        {
            try
            {
                string[] regKeys = {
                    @"SOFTWARE\Python\PythonCore",
                    @"SOFTWARE\WOW6432Node\Python\PythonCore"
                };

                foreach (var keyPath in regKeys)
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(keyPath);
                    if (baseKey != null)
                    {
                        foreach (var verName in baseKey.GetSubKeyNames())
                        {
                            using var verKey = baseKey.OpenSubKey($@"{verName}\InstallPath");
                            if (verKey != null)
                            {
                                var executable = verKey.GetValue("ExecutablePath")?.ToString();
                                if (!string.IsNullOrEmpty(executable) && File.Exists(executable))
                                    return executable;
                            }
                        }
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static async Task CheckPipAsync(EnvironmentCheckResult result, StringBuilder sb)
        {
            try
            {
                string pyCmd = !string.IsNullOrEmpty(result.PythonPath) ? $"\"{result.PythonPath}\"" : "python";
                var (success, output) = await RunCmdAsync(pyCmd, "-m pip --version");

                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    result.HasPip = true;
                    result.PipVersion = output.Trim();
                }
                else
                {
                    var (pipOk, pipOut) = await RunCmdAsync("pip", "--version");
                    if (pipOk && !string.IsNullOrWhiteSpace(pipOut))
                    {
                        result.HasPip = true;
                        result.PipVersion = pipOut.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[Pip Error] {ex.Message}");
            }

            if (result.HasPip)
            {
                sb.AppendLine($"Status: PASS | Version: {result.PipVersion}");
            }
            else
            {
                sb.AppendLine("Status: FAIL | pip is not installed or not in PATH.");
            }
        }

        private static async Task CheckCpuAsync(EnvironmentCheckResult result, StringBuilder sb)
        {
            try
            {
                // Registry query for CPU Name & MHz
                using (var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (cpuKey != null)
                    {
                        result.CpuName = cpuKey.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown CPU";
                        var mhzObj = cpuKey.GetValue("~MHz");
                        if (mhzObj != null)
                        {
                            int mhz = Convert.ToInt32(mhzObj);
                            result.CpuClockSpeed = mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : $"{mhz} MHz";
                        }
                    }
                }

                // Average load query via PowerShell / Performance API
                var (loadOk, loadOut) = await RunCmdAsync("powershell", "-NoProfile -Command \"(Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average\"");
                if (loadOk && float.TryParse(loadOut.Trim(), out float avgLoad))
                {
                    result.CpuAverageLoad = avgLoad;
                }
                else
                {
                    result.CpuAverageLoad = -1;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[CPU Query Warning] {ex.Message}");
                if (string.IsNullOrEmpty(result.CpuName)) result.CpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Generic CPU";
            }

            sb.AppendLine($"CPU Model: {result.CpuName}");
            sb.AppendLine($"Clock Speed: {result.CpuClockSpeed}");
            sb.AppendLine(result.CpuAverageLoad >= 0 ? $"Average Load: {result.CpuAverageLoad:F1}%" : "Average Load: N/A");
        }

        private static async Task CheckGpuAndCudaAsync(EnvironmentCheckResult result, StringBuilder sb)
        {
            try
            {
                // NVIDIA SMI check
                var (smiOk, smiOut) = await RunCmdAsync("nvidia-smi", "--query-gpu=name,memory.total,driver_version,cuda_version --format=csv,noheader");
                if (smiOk && !string.IsNullOrWhiteSpace(smiOut))
                {
                    var parts = smiOut.Split(',');
                    if (parts.Length >= 4)
                    {
                        result.GpuName = parts[0].Trim();
                        result.GpuMemory = parts[1].Trim();
                        result.HasCuda = true;
                        result.CudaVersion = $"CUDA v{parts[3].Trim()} (Driver {parts[2].Trim()})";
                    }
                    else
                    {
                        result.GpuName = smiOut.Trim();
                        result.HasCuda = true;
                        result.CudaVersion = "NVIDIA CUDA Detected";
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(result.GpuName))
            {
                // WMI fallback for GPU name
                try
                {
                    var (gpuOk, gpuOut) = await RunCmdAsync("powershell", "-NoProfile -Command \"(Get-CimInstance Win32_VideoController | Select-Object -First 1 -ExpandProperty Name)\"");
                    if (gpuOk && !string.IsNullOrWhiteSpace(gpuOut))
                    {
                        result.GpuName = gpuOut.Trim();
                    }
                }
                catch { }
            }

            // Check nvcuda.dll in System32
            if (!result.HasCuda)
            {
                string sys32Cuda = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll");
                if (File.Exists(sys32Cuda))
                {
                    result.HasCuda = true;
                    result.CudaVersion = "nvcuda.dll Present";
                }
            }

            sb.AppendLine($"GPU: {(string.IsNullOrEmpty(result.GpuName) ? "Integrated / Standard Display Adapter" : result.GpuName)}");
            if (!string.IsNullOrEmpty(result.GpuMemory)) sb.AppendLine($"VRAM: {result.GpuMemory}");
            sb.AppendLine($"CUDA Support: {(result.HasCuda ? $"YES ({result.CudaVersion})" : "NO (CPU Fallback Mode)")}");
        }

        private static async Task CheckLiveCaptionsBinaryAsync(EnvironmentCheckResult result, StringBuilder sb)
        {
            await Task.Yield();
            try
            {
                // 1. Check active process
                var processes = Process.GetProcessesByName("LiveCaptions");
                if (processes.Length > 0)
                {
                    result.IsLiveCaptionsRunning = true;
                    result.LiveCaptionsPid = processes[0].Id;
                    try
                    {
                        result.LiveCaptionsPath = processes[0].MainModule?.FileName ?? "Running Process";
                    }
                    catch
                    {
                        result.LiveCaptionsPath = "Running (Access Restricted)";
                    }
                    result.HasLiveCaptionsBinary = true;
                }

                // 2. Direct check for System32 path (C:\WINDOWS\system32\LiveCaptions.exe)
                string sys32LiveCaptions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "LiveCaptions.exe");
                if (File.Exists(sys32LiveCaptions))
                {
                    result.HasLiveCaptionsBinary = true;
                    if (string.IsNullOrEmpty(result.LiveCaptionsPath) || !result.IsLiveCaptionsRunning)
                    {
                        result.LiveCaptionsPath = sys32LiveCaptions;
                    }
                }

                // 3. Search WindowsApps folder if not found via process or system32
                if (!result.HasLiveCaptionsBinary)
                {
                    string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                    if (Directory.Exists(windowsApps))
                    {
                        try
                        {
                            var dirs = Directory.GetDirectories(windowsApps, "Microsoft.LiveCaptions_*");
                            foreach (var d in dirs)
                            {
                                string candidate = Path.Combine(d, "LiveCaptions.exe");
                                if (File.Exists(candidate))
                                {
                                    result.HasLiveCaptionsBinary = true;
                                    result.LiveCaptionsPath = candidate;
                                    break;
                                }
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // UAC restriction reading WindowsApps folder directly
                            sb.AppendLine("[Note] Access to C:\\Program Files\\WindowsApps\\ was restricted by Windows UAC.");
                        }
                    }
                }

                // 4. Search SystemApps / System32 subfolders
                if (!result.HasLiveCaptionsBinary)
                {
                    string sysApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
                    if (Directory.Exists(sysApps))
                    {
                        var matches = Directory.GetFiles(sysApps, "LiveCaptions.exe", SearchOption.AllDirectories);
                        if (matches.Length > 0)
                        {
                            result.HasLiveCaptionsBinary = true;
                            result.LiveCaptionsPath = matches[0];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[LiveCaptions Query Error] {ex.Message}");
            }

            if (result.HasLiveCaptionsBinary)
            {
                sb.AppendLine($"Binary Status: FOUND");
                sb.AppendLine($"Path: {(string.IsNullOrEmpty(result.LiveCaptionsPath) ? "Windows System Package" : result.LiveCaptionsPath)}");
                sb.AppendLine($"Process State: {(result.IsLiveCaptionsRunning ? $"RUNNING (PID: {result.LiveCaptionsPid})" : "Not Running")}");
            }
            else
            {
                sb.AppendLine("Binary Status: NOT FOUND");
                sb.AppendLine("Advice: Please enable Windows Live Captions feature in Windows 11 Settings (Accessibility -> Captions).");
            }
        }

        private static async Task<(bool success, string output)> RunCmdAsync(string fileName, string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = startInfo };
                var sbOut = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();

                await process.WaitForExitAsync();
                return (process.ExitCode == 0, sbOut.ToString());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
