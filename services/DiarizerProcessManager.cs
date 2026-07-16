using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MMslcOverlay.Services
{
    public class DiarizerProcessManager : IDisposable
    {
        private Process? _process;
        private StreamWriter? _stdin;
        private CancellationTokenSource? _cts;
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public event Action<DiarizerEvent>? OnEvent;
        public event Action<string>? OnLog;

        public async Task StartAsync(DiarizerConfig config, string pythonExePath, string scriptPath)
        {
            if (_process != null && !_process.HasExited)
            {
                throw new InvalidOperationException("Diarizer process is already running.");
            }

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

            _process.Start();
            _process.BeginErrorReadLine();
            
            _stdin = _process.StandardInput;

            _ = Task.Run(() => ReadOutputLoopAsync(_process.StandardOutput, _cts.Token, config.Debug), _cts.Token);
            
            await Task.CompletedTask;
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
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[CLI_FATAL] Error reading stdout: {ex.Message}");
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
        }
    }
}
