using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using m_mslc_overlay.core;
using System.Collections.Generic;
using System.Linq;

namespace m_mslc_overlay.services
{
    public class LiveCaptionPipeService : IDisposable
    {
        private const string PipeName = "LiveCaptionPipe";
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _listenerTask;
        private Task? _tickTask;
        
        private readonly AdaptiveCommitEngine _commitEngine;
        private readonly LiveCaptionUdpSender _udpSender;
        private string _lastRawText = "";
        private ulong _lastOffset = 0;

        // Pacing state
        private readonly List<double> _speechSpeedHistory = new();
        private const double DefaultAvgSS = 330.0;

        public bool IsRunning => _listenerTask != null && !_listenerTask.IsCompleted;

        public event Action<string>? OnPartialCaptionReceived;
        public event Action<CommitMetadata>? OnFinalSentenceReceived;
        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnError;

        public double AverageSpeechSpeed 
        {
            get 
            {
                lock (_speechSpeedHistory)
                {
                    return _speechSpeedHistory.Count > 0 ? _speechSpeedHistory.Average() : DefaultAvgSS;
                }
            }
        }

        public LiveCaptionPipeService()
        {
            _commitEngine = new AdaptiveCommitEngine();
            _udpSender = new LiveCaptionUdpSender();
        }

        public void Start()
        {
            if (_listenerTask != null && !_listenerTask.IsCompleted) return;

            _cancellationTokenSource = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenLoopAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
            _tickTask = Task.Run(() => TickLoopAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
            OnStatusChanged?.Invoke("Pipe listener started.");
        }

        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
            OnStatusChanged?.Invoke("Pipe listener stopped.");
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var pipeSecurity = new PipeSecurity();
                    
                    // Current user - full control
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        WindowsIdentity.GetCurrent().Name,
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));
                    
                    // ALL APPLICATION PACKAGES - cho phép AppContainer connect
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier("S-1-15-2-1"), 
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));

                    using var pipeServer = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        65536, 65536,
                        pipeSecurity);

                    OnStatusChanged?.Invoke("Waiting for HookCore connection...");
                    await pipeServer.WaitForConnectionAsync(token);
                    OnStatusChanged?.Invoke("Client connected to named pipe.");

                    _commitEngine.ResetState();
                    _lastRawText = "";
                    _lastOffset = 0;
                    lock (_speechSpeedHistory) { _speechSpeedHistory.Clear(); }

                    using var reader = new StreamReader(pipeServer, Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync(token)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        ProcessJsonLine(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal stop
                    break;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Pipe error: {ex.Message}");
                }
                finally
                {
                    OnStatusChanged?.Invoke("Client disconnected. Restarting pipe in 1s...");
                    try { await Task.Delay(1000, token); } catch { }
                }
            }
        }

        private void ProcessJsonLine(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                
                string text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                bool isFinal = root.TryGetProperty("is_final", out var finalProp) && finalProp.GetBoolean();
                ulong offset = root.TryGetProperty("offset", out var offsetProp) ? offsetProp.GetUInt64() : 0;
                ulong duration = root.TryGetProperty("duration", out var durProp) ? durProp.GetUInt64() : 0;

                _udpSender.SendSyncIfNeeded(offset);
                
                if (string.IsNullOrWhiteSpace(text)) {
                    _udpSender.SetState("IDLE");
                } else {
                    _udpSender.SetState("PENDING");
                }

                OnPartialCaptionReceived?.Invoke(text);

                double wallClockMs = Environment.TickCount64;
                // acousticEndMs = endpoint của âm thanh cuối trong packet này (100ns ticks → ms)
                // Fallback về wall-clock nếu SDK không cung cấp offset (edge case: offset=0)
                double acousticEndMs = (offset > 0 && duration > 0)
                    ? (double)(offset + duration) / 10_000.0
                    : wallClockMs;

                // Handle Offset Changes (force flush old sentence)
                // S19 guard: only trigger on significant offset jumps (>50ms = 500000 ticks)
                // to avoid spurious flushes from SDK offset jitter within the same utterance.
                bool isSignificantOffsetChange = offset != 0 && _lastOffset != 0
                    && offset != _lastOffset
                    && Math.Abs((long)offset - (long)_lastOffset) > 500_000;

                if (isSignificantOffsetChange)
                {
                    var flushResult = _commitEngine.Evaluate(_lastRawText, acousticEndMs, wallClockMs, true);
                    if (flushResult.Type != CommitType.None && !string.IsNullOrWhiteSpace(flushResult.CommittedText))
                    {
                        var commitMeta = CommitMetadata.From(
                            flushResult.CommittedText, "OffsetChange",
                            acousticEndMs: acousticEndMs, utteranceOffset: _lastOffset);
                        _udpSender.SendCommit(commitMeta);
                        OnFinalSentenceReceived?.Invoke(commitMeta);
                    }
                    _lastRawText = "";
                }
                if (offset != 0) _lastOffset = offset;

                // Pacing computation
                if (!isFinal && duration > 0 && !string.IsNullOrWhiteSpace(text))
                {
                    double ms = duration / 10000.0;
                    int wordCount = text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    if (wordCount >= 3)
                    {
                        double msPerWord = ms / wordCount;
                        if (msPerWord >= 100 && msPerWord <= 1500)
                        {
                            lock (_speechSpeedHistory)
                            {
                                _speechSpeedHistory.Add(msPerWord);
                                if (_speechSpeedHistory.Count > 15) _speechSpeedHistory.RemoveAt(0);
                            }
                        }
                    }
                }

                // Core SSACE Evaluation
                var result = _commitEngine.Evaluate(text, acousticEndMs, wallClockMs, isFinal);
                if (result.Type != CommitType.None && !string.IsNullOrWhiteSpace(result.CommittedText))
                {
                    string reason = result.Type == CommitType.Hard ? "HardCommit" : "SoftCommit";
                    var commitMeta = CommitMetadata.From(
                        result.CommittedText, reason,
                        acousticEndMs: acousticEndMs,
                        utteranceOffset: offset,
                        isDangling: result.IsDangling);
                    _udpSender.SendCommit(commitMeta);
                    OnFinalSentenceReceived?.Invoke(commitMeta);
                }

                // Sau FINAL, engine đã ResetState() — _lastRawText phải sạch để
                // OffsetChange trên packet kế tiếp không flush lại text đã committed.
                _lastRawText = isFinal ? "" : text;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"JSON parsing error: {ex.Message}");
            }
        }

        private async Task TickLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Active polling loop (50-100ms) for AdaptiveCommitEngine Debounce
                    await Task.Delay(50, token);
                    
                    double arrivalTimeMs = Environment.TickCount64;
                    var result = _commitEngine.CheckDebounceTimeout(arrivalTimeMs);
                    if (result.Type != CommitType.None && !string.IsNullOrWhiteSpace(result.CommittedText))
                    {
                        var commitMeta = CommitMetadata.From(
                            result.CommittedText, "DebounceCommit",
                            isDangling: result.IsDangling);
                        _udpSender.SendCommit(commitMeta);
                        OnFinalSentenceReceived?.Invoke(commitMeta);
                    }
                    
                    // Gửi heartbeat 50ms một lần (có thể là tick cho UI)
                    // C# tick 50ms, Python gate logic ko gặp vde
                    _udpSender.SendStateHeartbeat();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Tick error: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _udpSender?.Dispose();
        }
    }
}
