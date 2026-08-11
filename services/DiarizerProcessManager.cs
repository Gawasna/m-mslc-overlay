using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MMslcOverlay.Services
{
    public enum DiarizerState
    {
        Stopped,
        Starting,
        Ready,
        Failed
    }

    public class DiarizerProcessManager : IDisposable
    {
        private Process? _process;
        private StreamWriter? _stdin;
        private CancellationTokenSource? _cts;
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        // Used during pre-warm: process is started and ready but audio stream
        // is paused until user actually starts a session.
        public bool IsPausedForSession { get; private set; } = false;

        public event Action<DiarizerEvent>? OnEvent;
        public event Action<string>? OnLog;

        public static DiarizerState GlobalState { get; private set; } = DiarizerState.Stopped;
        public static event Action<DiarizerState>? OnGlobalStateChanged;
        
        private static void UpdateGlobalState(DiarizerState newState)
        {
            if (GlobalState != newState)
            {
                GlobalState = newState;
                OnGlobalStateChanged?.Invoke(newState);
            }
        }

        public async Task StartAsync(DiarizerConfig config, string pythonExePath, string scriptPath)
        {
            if (_process != null && !_process.HasExited)
            {
                throw new InvalidOperationException("Diarizer process is already running.");
            }

            UpdateGlobalState(DiarizerState.Starting);

            _cts = new CancellationTokenSource();

            string args = $"\"{scriptPath}\" --device {config.DeviceIndex} --db_path \"{config.DbPath}\" --lc_port {config.LcPort}";
            if (config.Debug)
            {
                args += " --debug";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExePath,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty
            };

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            
            _process.ErrorDataReceived += (s, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    OnLog?.Invoke($"[CLI_ERR] {e.Data}");
                }
            };

            // Bug 2: Use TaskCompletionSource so that StartAsync truly awaits until
            // the Python engine emits {"type":"ready"} — avoids sending audio before
            // the model is loaded which caused the long perceived startup delay.
            var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Wire a one-shot handler that signals the TCS on ReadyEvent
            Action<DiarizerEvent>? readyHandler = null;
            readyHandler = (evt) =>
            {
                if (evt is ReadyEvent)
                {
                    readyTcs.TrySetResult(true);
                    OnEvent -= readyHandler; // unsubscribe after first fire
                }
            };
            OnEvent += readyHandler;

            _process.Start();
            _process.BeginErrorReadLine();
            
            _stdin = _process.StandardInput;

            _ = Task.Run(() => ReadOutputLoopAsync(_process.StandardOutput, _cts.Token, config.Debug), _cts.Token);
            
            // Wait for ready signal with a 45-second timeout (model load can be slow on first run)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await readyTcs.Task.WaitAsync(timeoutCts.Token);
                OnLog?.Invoke("[DIARIZER] Engine ready signal received.");
            }
            catch (OperationCanceledException)
            {
                OnLog?.Invoke("[DIARIZER] Timeout waiting for ready signal — engine may still be loading.");
                // Non-fatal: let execution continue; events will still fire when ready
                OnEvent -= readyHandler;
            }
        }

        /// <summary>
        /// Pre-warm: start process and immediately pause the audio stream.
        /// Call this at app startup so the model is loaded before the user presses Start Session.
        /// </summary>
        public async Task StartPreWarmedAsync(DiarizerConfig config, string pythonExePath, string scriptPath)
        {
            await StartAsync(config, pythonExePath, scriptPath);
            // Pause audio immediately — we only wanted the model to load, not to capture audio yet
            await SendCommandAsync(new { cmd = "pause_audio" });
            IsPausedForSession = true;
            OnLog?.Invoke("[DIARIZER] Pre-warmed: model loaded, audio stream paused until session starts.");
        }

        /// <summary>Resume audio after pre-warm or soft-pause.</summary>
        public async Task ResumeAudioAsync()
        {
            await SendCommandAsync(new { cmd = "resume_audio" });
            IsPausedForSession = false;
        }

        private async Task ReadOutputLoopAsync(StreamReader stdout, CancellationToken ct, bool debug)
        {
            try
            {
                while (!ct.IsCancellationRequested && !stdout.EndOfStream)
                {
                    var line = await stdout.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (debug && !line.Contains("\"type\": \"vol_level\"") && !line.Contains("\"type\":\"vol_level\""))
                    {
                        OnLog?.Invoke($"[CLI_IPC_RAW] {line}");
                    }

                    try
                    {
                        var diarizerEvent = JsonSerializer.Deserialize<DiarizerEvent>(line, _jsonOptions);
                        if (diarizerEvent != null)
                        {
                            if (diarizerEvent is ReadyEvent)
                            {
                                UpdateGlobalState(DiarizerState.Ready);
                            }
                            OnEvent?.Invoke(diarizerEvent);
                        }
                    }
                    catch (JsonException ex)
                    {
                        OnLog?.Invoke($"[CLI_JSON_ERR] Failed to parse: {line}. Exception: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal exit
                UpdateGlobalState(DiarizerState.Stopped);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[CLI_FATAL] Error reading stdout: {ex.Message}");
                UpdateGlobalState(DiarizerState.Failed);
            }
        }

        public async Task SendCommandAsync(object command)
        {
            if (_stdin == null || _process == null || _process.HasExited)
            {
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(command);
                await _stdin.WriteLineAsync(json);
                await _stdin.FlushAsync();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[CLI_CMD_ERR] Failed to send command: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            if (_process == null || _process.HasExited)
            {
                return;
            }

            await SendCommandAsync(new { cmd = "stop" });
            
            // Wait for graceful exit
            if (!_process.WaitForExit(3000))
            {
                _process.Kill();
            }

            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            _cts?.Dispose();
            _cts = null;
            _process.Dispose();
            _process = null;
            _stdin = null;
            UpdateGlobalState(DiarizerState.Stopped);
        }

        public void Dispose()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                try { _cts.Cancel(); } catch { }
            }
            _cts?.Dispose();
            _cts = null;
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
                _process.Dispose();
            }
            UpdateGlobalState(DiarizerState.Stopped);
        }
    }
}
