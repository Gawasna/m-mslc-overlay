using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    /// <summary>
    /// Creates per-atom venv and installs requirements.txt so plugins can actually run.
    /// atom26 → installDir/venv ; atom32 → installDir/.venv (matches existing managers).
    /// </summary>
    public static class PluginPythonEnvService
    {
        public static string GetVenvDirName(string atomId)
            => string.Equals(atomId, "atom32", StringComparison.OrdinalIgnoreCase) ? ".venv" : "venv";

        public static string GetVenvPythonPath(string installDir, string atomId)
            => Path.Combine(installDir, GetVenvDirName(atomId), "Scripts", "python.exe");

        public static bool IsEnvReady(string installDir, string atomId)
            => File.Exists(GetVenvPythonPath(installDir, atomId));

        /// <summary>
        /// Resolve host Python for creating venv (py -3 / python).
        /// </summary>
        public static async Task<string?> FindHostPythonAsync(Action<string>? onLog = null)
        {
            // Prefer py launcher
            var py = await TryRunCaptureAsync("py", "-3 -c \"import sys; print(sys.executable)\"", onLog);
            if (!string.IsNullOrWhiteSpace(py) && File.Exists(py.Trim()))
                return py.Trim();

            py = await TryRunCaptureAsync("python", "-c \"import sys; print(sys.executable)\"", onLog);
            if (!string.IsNullOrWhiteSpace(py) && File.Exists(py.Trim()))
                return py.Trim();

            onLog?.Invoke("[PluginPythonEnv] Host Python 3 not found (py -3 / python).");
            return null;
        }

        /// <summary>
        /// Ensure venv exists and requirements are installed. Idempotent if venv already present
        /// unless forcePip is true.
        /// </summary>
        public static async Task<bool> EnsureEnvAsync(
            string atomId,
            string installDir,
            Action<string> onLog,
            Action<double>? onProgress = null,
            bool forcePip = false)
        {
            if (!Directory.Exists(installDir))
            {
                onLog($"[PluginPythonEnv] Install dir missing: {installDir}");
                return false;
            }

            string requirements = Path.Combine(installDir, "requirements.txt");
            string venvPython = GetVenvPythonPath(installDir, atomId);
            string venvDir = Path.Combine(installDir, GetVenvDirName(atomId));

            onProgress?.Invoke(5);

            if (!File.Exists(venvPython))
            {
                string? hostPy = await FindHostPythonAsync(onLog);
                if (hostPy == null) return false;

                onLog($"[PluginPythonEnv] Creating venv with {hostPy} → {venvDir}");
                onProgress?.Invoke(15);

                int code = await RunProcessAsync(hostPy, $"-m venv \"{venvDir}\"", installDir, onLog);
                if (code != 0 || !File.Exists(venvPython))
                {
                    onLog("[PluginPythonEnv] venv creation failed.");
                    return false;
                }
                onLog("[PluginPythonEnv] venv created.");
                forcePip = true;
            }
            else
            {
                onLog($"[PluginPythonEnv] venv already exists: {venvPython}");
            }

            onProgress?.Invoke(40);

            if (!File.Exists(requirements))
            {
                onLog("[PluginPythonEnv] No requirements.txt — skip pip.");
                onProgress?.Invoke(100);
                return true;
            }

            // Marker so we don't re-pip every launch unless forced / requirements newer
            string stamp = Path.Combine(venvDir, ".mslc_pip_ok");
            if (!forcePip && File.Exists(stamp))
            {
                var reqTime = File.GetLastWriteTimeUtc(requirements);
                var stampTime = File.GetLastWriteTimeUtc(stamp);
                if (stampTime >= reqTime)
                {
                    onLog("[PluginPythonEnv] Dependencies already installed (stamp ok).");
                    onProgress?.Invoke(100);
                    return true;
                }
            }

            onLog("[PluginPythonEnv] pip install -r requirements.txt (may take several minutes)...");
            onProgress?.Invoke(50);

            // Upgrade pip first (best effort)
            await RunProcessAsync(venvPython, "-m pip install --upgrade pip", installDir, onLog);

            int pipCode = await RunProcessAsync(
                venvPython,
                $"-m pip install -r \"{requirements}\"",
                installDir,
                onLog);

            // atom32 also needs pyaudiowpatch (loopback) — not always listed
            if (string.Equals(atomId, "atom32", StringComparison.OrdinalIgnoreCase))
            {
                onLog("[PluginPythonEnv] Ensuring pyaudiowpatch for WASAPI loopback...");
                await RunProcessAsync(venvPython, "-m pip install pyaudiowpatch", installDir, onLog);
            }

            if (pipCode != 0)
            {
                onLog("[PluginPythonEnv] pip install failed (see log above).");
                return false;
            }

            try { File.WriteAllText(stamp, DateTime.UtcNow.ToString("O")); } catch { /* ignore */ }

            // atom32: download ONNX models if missing (silero_vad / campplus)
            if (string.Equals(atomId, "atom32", StringComparison.OrdinalIgnoreCase))
            {
                string downloader = Path.Combine(installDir, "model_downloader.py");
                string vad = Path.Combine(installDir, "models", "silero_vad.onnx");
                if (File.Exists(downloader) && !File.Exists(vad))
                {
                    onLog("[PluginPythonEnv] Downloading atom32 ONNX models (model_downloader.py)...");
                    int mdl = await RunProcessAsync(venvPython, $"\"{downloader}\"", installDir, onLog);
                    if (mdl != 0)
                        onLog("[PluginPythonEnv] Model download reported errors — check network / HF access.");
                }
                else if (File.Exists(vad))
                {
                    onLog("[PluginPythonEnv] ONNX models already present.");
                }
            }

            onProgress?.Invoke(100);
            onLog("[PluginPythonEnv] Environment ready.");
            return true;
        }

        private static async Task<string?> TryRunCaptureAsync(string fileName, string args, Action<string>? onLog)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string stdout = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) return null;
                return stdout.Trim();
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[PluginPythonEnv] {fileName} not usable: {ex.Message}");
                return null;
            }
        }

        private static async Task<int> RunProcessAsync(string fileName, string args, string workDir, Action<string> onLog)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var sb = new StringBuilder();
                p.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    onLog("  " + e.Data);
                };
                p.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    onLog("  " + e.Data);
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                onLog($"[PluginPythonEnv] Failed to run {fileName} {args}: {ex.Message}");
                return -1;
            }
        }
    }
}
