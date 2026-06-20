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

namespace m_mslc_overlay.services
{
    public class LiveCaptionPipeService : IDisposable
    {
        private const string PipeName = "LiveCaptionPipe";
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _listenerTask;
        private readonly SentenceSplitter _splitter;
        private string _shortSentenceBuffer = "";

        public event Action<string>? OnPartialCaptionReceived;
        public event Action<string>? OnFinalSentenceReceived;
        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnError;

        public LiveCaptionPipeService()
        {
            _splitter = new SentenceSplitter();
        }

        public void Start()
        {
            if (_listenerTask != null && !_listenerTask.IsCompleted) return;

            _cancellationTokenSource = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenLoopAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
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

                    _splitter.Reset();

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

                OnPartialCaptionReceived?.Invoke(text);

                var sentences = _splitter.ExtractNewSentences(text, isFinal);
                foreach (var s in sentences)
                {
                    string combined = string.IsNullOrEmpty(_shortSentenceBuffer) ? s : _shortSentenceBuffer + " " + s;
                    int wordCount = combined.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    
                    if (wordCount <= 3)
                    {
                        _shortSentenceBuffer = combined;
                    }
                    else
                    {
                        OnFinalSentenceReceived?.Invoke(combined);
                        _shortSentenceBuffer = "";
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"JSON parsing error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
