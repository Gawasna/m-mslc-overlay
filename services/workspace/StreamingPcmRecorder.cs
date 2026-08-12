using System;
using System.IO;
using System.Text.Json;
using MMslcOverlay.Core.Workspace.Models;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Ghi audio streaming từ Output Device (WASAPI Loopback) thành PCM chunks (crash-safe, append-only, 16kHz Mono 16-bit)
/// </summary>
public class StreamingPcmRecorder : IDisposable
{
    private readonly string _sessionDir;
    private readonly SessionMetadata _metadata;
    private FileStream? _currentChunkStream;
    private BinaryWriter? _currentChunkWriter;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _silencePlayer;
    private double _resamplePos = 0;
    private long _lastSavedOffsetMs = 0;
    private int _currentChunkId = 0;
    private long _currentChunkBytes = 0;
    private long _sessionOffsetMs = 0;
    private bool _isRecording;
    
    // Anchor for syncing audio timeline with STT SDK timeline.
    // Phase 1: snapshot recorder offset the moment the first partial arrives (pre-commit).
    // Phase 2: confirm anchor with SDK tsStartMs on first commit.
    private long _anchorAudioMs = -1;       // recorder offsetMs at anchor point (= audio file position of first utterance start)
    private long _anchorTsMs = -1;          // STT SDK TsStartMs at anchor point
    private long _firstPartialSnapshotMs = -1; // _sessionOffsetMs captured at first non-empty partial
    
    // Chunk thresholds
    private const long MAX_CHUNK_SIZE_BYTES = 500_000_000;  // 500MB
    private const long BYTES_PER_MS = 32;  // 16kHz, 16-bit, Mono = 32 bytes/ms

    public string SessionId => _metadata.SessionId;
    public long CurrentOffsetMs => _sessionOffsetMs;

    public StreamingPcmRecorder(string audioDir, string sessionId)
    {
        _sessionDir = Path.Combine(audioDir, sessionId);
        Directory.CreateDirectory(_sessionDir);
        
        _metadata = new SessionMetadata
        {
            SessionId = sessionId,
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SampleRate = 16000,
            BitsPerSample = 16,
            Channels = 1,
            Chunks = new System.Collections.Generic.List<ChunkInfo>(),
            Status = "recording"
        };
        
        StartNewChunk();
    }
    
    private void StartNewChunk()
    {
        // Close and finalize previous chunk
        if (_currentChunkWriter != null)
        {
            FinalizeCurrentChunk();
            _currentChunkWriter.Dispose();
            _currentChunkStream?.Dispose();
        }
        
        _currentChunkId++;
        string fileName = $"chunk_{_currentChunkId:D3}.pcm";
        string filePath = Path.Combine(_sessionDir, fileName);
        
        _currentChunkStream = new FileStream(
            filePath, 
            FileMode.Create, 
            FileAccess.Write, 
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.WriteThrough  // Bypass OS cache for immediate flush
        );
        _currentChunkWriter = new BinaryWriter(_currentChunkStream);
        _currentChunkBytes = 0;
        
        _metadata.Chunks.Add(new ChunkInfo
        {
            ChunkId = _currentChunkId,
            FileName = fileName,
            StartOffsetMs = _sessionOffsetMs,
            Status = "active"
        });
        
        SaveMetadata();
        System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Started chunk {_currentChunkId}: {fileName}");
    }
    
    public void StartRecording()
    {
        if (_isRecording) return;
        
        try
        {
            _capture?.Dispose();
            _silencePlayer?.Dispose();
            _resamplePos = 0;
            
            _capture = new WasapiLoopbackCapture();
            
            // Mở luồng âm thanh câm (silence) ở shared mode để đảm bảo WASAPI Loopback tiếp tục trả về PCM silence liên tục khi system audio im lặng
            try
            {
                _silencePlayer = new WasapiOut(AudioClientShareMode.Shared, 100);
                _silencePlayer.Init(new SilentWaveProvider(_capture.WaveFormat));
                _silencePlayer.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamingPcmRecorder] Note: Could not start silent generator: {ex.Message}");
            }
            
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            
            _isRecording = true;
            Console.WriteLine($"[StreamingPcmRecorder] ✅ Recording started via WASAPI Loopback (Output Device) for session {SessionId} -> {_capture.WaveFormat}");
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Recording started for session {SessionId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StreamingPcmRecorder] ❌ Failed to start recording: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Failed to start recording: {ex.Message}");
            throw;
        }
    }
    
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording || _currentChunkWriter == null || _capture == null || e.BytesRecorded <= 0) return;
        
        try
        {
            byte[] pcm16kMono = ResampleTo16kMonoPcm16(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
            if (pcm16kMono.Length == 0) return;

            // Check if need to start new chunk
            if (_currentChunkBytes + pcm16kMono.Length > MAX_CHUNK_SIZE_BYTES)
            {
                StartNewChunk();
            }
            
            // Write PCM data (append-only, immediate flush)
            _currentChunkWriter.Write(pcm16kMono, 0, pcm16kMono.Length);
            _currentChunkWriter.Flush();
            _currentChunkStream?.Flush(flushToDisk: true);  // Force physical write
            
            _currentChunkBytes += pcm16kMono.Length;
            _sessionOffsetMs += pcm16kMono.Length / BYTES_PER_MS;

            if (_sessionOffsetMs - _lastSavedOffsetMs >= 1000)
            {
                if (_metadata.Chunks.Count > 0)
                {
                    var activeChunk = _metadata.Chunks[_metadata.Chunks.Count - 1];
                    activeChunk.SizeBytes = _currentChunkBytes;
                    activeChunk.DurationMs = _currentChunkBytes / BYTES_PER_MS;
                }
                _metadata.TotalDurationMs = _sessionOffsetMs;
                SaveMetadata();
                _lastSavedOffsetMs = _sessionOffsetMs;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StreamingPcmRecorder] Error in OnDataAvailable: {ex.Message}");
        }
    }
    
    private byte[] ResampleTo16kMonoPcm16(byte[] buffer, int bytesRecorded, WaveFormat inputFormat)
    {
        int channels = inputFormat.Channels;
        bool isFloat = (inputFormat.Encoding == WaveFormatEncoding.IeeeFloat || inputFormat.BitsPerSample == 32);
        bool isPcm16 = (inputFormat.Encoding == WaveFormatEncoding.Pcm && inputFormat.BitsPerSample == 16);
        
        int bytesPerFrame = isFloat ? (4 * channels) : (isPcm16 ? (2 * channels) : 0);
        if (bytesPerFrame == 0 || bytesRecorded < bytesPerFrame) return Array.Empty<byte>();
        
        int totalFrames = bytesRecorded / bytesPerFrame;
        float[] monoBuffer = new float[totalFrames];
        int offset = 0;
        
        if (isFloat)
        {
            for (int i = 0; i < totalFrames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToSingle(buffer, offset);
                    offset += 4;
                }
                monoBuffer[i] = sum / channels;
            }
        }
        else
        {
            for (int i = 0; i < totalFrames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    short val = BitConverter.ToInt16(buffer, offset);
                    sum += val / 32768f;
                    offset += 2;
                }
                monoBuffer[i] = sum / channels;
            }
        }
        
        double ratio = (double)inputFormat.SampleRate / 16000.0;
        int maxOutSamples = (int)Math.Ceiling(totalFrames / ratio) + 2;
        byte[] outBytes = new byte[maxOutSamples * 2];
        int outIndex = 0;

        while (_resamplePos < totalFrames)
        {
            int idx1 = (int)_resamplePos;
            int idx2 = Math.Min(idx1 + 1, totalFrames - 1);
            double frac = _resamplePos - idx1;
            
            float sample = (float)(monoBuffer[idx1] * (1.0 - frac) + monoBuffer[idx2] * frac);
            short shortVal = (short)Math.Clamp(Math.Round(sample * 32767.0f), -32768, 32767);
            
            if (outIndex + 1 < outBytes.Length)
            {
                outBytes[outIndex++] = (byte)(shortVal & 0xFF);
                outBytes[outIndex++] = (byte)((shortVal >> 8) & 0xFF);
            }
            
            _resamplePos += ratio;
        }

        _resamplePos -= totalFrames;

        if (outIndex == outBytes.Length) return outBytes;
        byte[] trimmed = new byte[outIndex];
        Array.Copy(outBytes, 0, trimmed, 0, outIndex);
        return trimmed;
    }

    public void StopRecording()
    {
        if (!_isRecording) return;
        
        try
        {
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;

            _silencePlayer?.Stop();
            _silencePlayer?.Dispose();
            _silencePlayer = null;
            
            _isRecording = false;
            
            FinalizeCurrentChunk();
            _currentChunkWriter?.Dispose();
            _currentChunkStream?.Dispose();
            _currentChunkWriter = null;
            _currentChunkStream = null;
            
            _metadata.TotalDurationMs = _sessionOffsetMs;
            _metadata.Status = "completed";
            
            SaveMetadata();
            
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Recording stopped. Total duration: {_sessionOffsetMs}ms");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Error stopping recording: {ex.Message}");
        }
    }
    
    private class SilentWaveProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; private set; }
        public SilentWaveProvider(WaveFormat format) { WaveFormat = format; }
        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
    
    private void FinalizeCurrentChunk()
    {
        if (_metadata.Chunks.Count > 0)
        {
            var chunk = _metadata.Chunks[_metadata.Chunks.Count - 1];
            chunk.SizeBytes = _currentChunkBytes;
            chunk.DurationMs = _currentChunkBytes / BYTES_PER_MS;
            chunk.Status = "finalized";
            
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Finalized chunk {chunk.ChunkId}: {chunk.SizeBytes} bytes, {chunk.DurationMs}ms");
        }
    }
    
    private void SaveMetadata()
    {
        try
        {
            string metadataPath = Path.Combine(_sessionDir, "metadata.json");
            string json = JsonSerializer.Serialize(_metadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metadataPath, json);
            
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Metadata saved to {metadataPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Failed to save metadata: {ex.Message}");
        }
    }
    
    public (string sessionId, long offsetMs) GetCurrentReference()
    {
        return (SessionId, _sessionOffsetMs);
    }
    
    /// <summary>
    /// Phase 1: snapshot recorder position the instant the first non-empty partial arrives.
    /// Must be called BEFORE any commit so we capture the audio file position at utterance onset,
    /// not back-calculated from commit time (which introduces SDK-duration vs real-duration mismatch).
    /// </summary>
    public void SnapshotOffsetAtFirstPartial()
    {
        if (_firstPartialSnapshotMs >= 0) return;
        _firstPartialSnapshotMs = _sessionOffsetMs;
    }

    /// <summary>
    /// Confirm STT↔audio anchor on first commit.
    /// Prefer first-partial snapshot; else back-calculate from session position.
    /// </summary>
    public void SetFirstUtteranceAnchor(long tsStartMs, long tsEndMs)
    {
        if (_anchorAudioMs >= 0) return;

        if (_firstPartialSnapshotMs >= 0)
        {
            _anchorAudioMs = _firstPartialSnapshotMs;
        }
        else
        {
            long utteranceDurationMs = Math.Max(0, tsEndMs - tsStartMs);
            _anchorAudioMs = Math.Max(0, _sessionOffsetMs - utteranceDurationMs);
        }
        _anchorTsMs = tsStartMs;
    }
    
    /// <summary>
    /// Tính audioStartMs cho segment có tsStartMs bất kỳ, dựa vào anchor đã set.
    /// Trả về -1 nếu anchor chưa được set.
    /// </summary>
    public long AudioOffsetForTs(long tsStartMs)
    {
        if (_anchorAudioMs < 0) return -1;
        long computed = _anchorAudioMs + (tsStartMs - _anchorTsMs);
        return Math.Max(0, computed);
    }
    
    public bool HasAnchor => _anchorAudioMs >= 0;
    
    public void Dispose()
    {
        StopRecording();
    }
}
