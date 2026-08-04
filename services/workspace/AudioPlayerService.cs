using System;
using System.IO;
using NAudio.Wave;
using NAudio.CoreAudioApi; // For WasapiOut

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Phát một đoạn audio cụ thể từ file WAV hoặc session-based PCM chunks.
/// Thread-safe: cancel playback hiện tại trước khi phát mới.
/// </summary>
public class AudioPlayerService : IDisposable
{
    private IWavePlayer? _waveOut;
    private readonly object _lock = new();

    public event Action<string>? PlaybackEnded; // segId
    public event Action<string>? PlaybackStarted; // segId

    /// <summary>
    /// Play segment from session-based PCM chunks (NEW API for Phase 2)
    /// </summary>
    public void PlaySegmentByTime(string segId, string sessionDir, long offsetMs, long durationMs)
    {
        SessionLogger.Log($"[AudioPlayerService] ===== PlaySegmentByTime CALLED =====");
        SessionLogger.Log($"[AudioPlayerService]   segId: {segId}");
        SessionLogger.Log($"[AudioPlayerService]   sessionDir: {sessionDir}");
        SessionLogger.Log($"[AudioPlayerService]   offsetMs: {offsetMs}");
        SessionLogger.Log($"[AudioPlayerService]   durationMs: {durationMs}");
        
        if (!Directory.Exists(sessionDir))
        {
            SessionLogger.Log($"[AudioPlayerService] ❌ Session directory NOT FOUND: {sessionDir}");
            PlaybackEnded?.Invoke(segId);
            return;
        }
        
        SessionLogger.Log($"[AudioPlayerService] ✅ Session directory exists");

        lock (_lock)
        {
            SessionLogger.Log($"[AudioPlayerService] Acquired lock, stopping current playback...");
            StopCurrent();
            SessionLogger.Log($"[AudioPlayerService] Current playback stopped");

            try
            {
                SessionLogger.Log($"[AudioPlayerService] Loading VirtualWavReader from {sessionDir}...");
                
                // Load virtual WAV reader from session chunks
                var reader = VirtualWavReader.FromSessionDir(sessionDir);
                
                SessionLogger.Log($"[AudioPlayerService] ✅ Reader loaded successfully");
                SessionLogger.Log($"[AudioPlayerService]   WaveFormat: {reader.WaveFormat}");
                SessionLogger.Log($"[AudioPlayerService]   Length: {reader.Length} bytes");
                
                // Seek to offset
                SessionLogger.Log($"[AudioPlayerService] Seeking to {offsetMs}ms...");
                reader.SeekToMilliseconds(offsetMs);
                SessionLogger.Log($"[AudioPlayerService] ✅ Seeked to position {reader.Position} bytes");
                
                // Calculate byte count for duration
                long bytesPerMs = reader.WaveFormat.AverageBytesPerSecond / 1000;
                long byteCount = bytesPerMs * durationMs;
                
                SessionLogger.Log($"[AudioPlayerService] Will play {byteCount} bytes ({durationMs}ms)");
                SessionLogger.Log($"[AudioPlayerService]   BytesPerMs: {bytesPerMs}");
                
                // Wrap in limiter to play only duration
                SessionLogger.Log($"[AudioPlayerService] Creating LimitedWaveStream...");
                var limiter = new LimitedWaveStream(reader, byteCount);
                SessionLogger.Log($"[AudioPlayerService] ✅ LimitedWaveStream created");
                
                // Use WasapiOut with shared mode to allow concurrent access with atom32
                SessionLogger.Log($"[AudioPlayerService] Creating WasapiOut (Shared mode)...");
                var wasapiOut = new WasapiOut(AudioClientShareMode.Shared, 100);
                _waveOut = wasapiOut;
                
                // Set volume to 100%
                _waveOut.Volume = 1.0f;
                
                SessionLogger.Log($"[AudioPlayerService] ✅ WasapiOut created (Shared mode, Volume: 100%)");
                
                SessionLogger.Log($"[AudioPlayerService] Initializing WasapiOut with limiter...");
                _waveOut.Init(limiter);
                SessionLogger.Log($"[AudioPlayerService] ✅ WaveOut initialized");
                
                _waveOut.PlaybackStopped += (s, e) =>
                {
                    SessionLogger.Log($"[AudioPlayerService] 🛑 PlaybackStopped event fired for {segId}");
                    if (e.Exception != null)
                    {
                        SessionLogger.Log($"[AudioPlayerService] ❌ Playback exception: {e.Exception.Message}");
                    }
                    
                    // Dispose in order
                    try
                    {
                        reader.Dispose();
                        limiter.Dispose();
                    }
                    catch (Exception disposeEx)
                    {
                        SessionLogger.Log($"[AudioPlayerService] ⚠️ Dispose error: {disposeEx.Message}");
                    }
                    
                    PlaybackEnded?.Invoke(segId);
                };
                
                SessionLogger.Log($"[AudioPlayerService] Firing PlaybackStarted event for {segId}...");
                PlaybackStarted?.Invoke(segId);
                SessionLogger.Log($"[AudioPlayerService] ✅ PlaybackStarted event fired");
                
                SessionLogger.Log($"[AudioPlayerService] Calling _waveOut.Play()...");
                SessionLogger.Log($"[AudioPlayerService] Playback state BEFORE Play(): {_waveOut.PlaybackState}");
                _waveOut.Play();
                SessionLogger.Log($"[AudioPlayerService] Playback state AFTER Play(): {_waveOut.PlaybackState}");
                SessionLogger.Log($"[AudioPlayerService] ✅ _waveOut.Play() completed - audio should be playing now!");
            }
            catch (Exception ex)
            {
                SessionLogger.Log($"[AudioPlayerService] ❌❌❌ EXCEPTION: {ex.Message}");
                SessionLogger.Log($"[AudioPlayerService] ❌ Stack trace: {ex.StackTrace}");
                PlaybackEnded?.Invoke(segId);
            }
        }
        
        SessionLogger.Log($"[AudioPlayerService] ===== PlaySegmentByTime COMPLETED =====");
    }

    /// <summary>
    /// Legacy API: Play segment from single WAV file with byte offset
    /// </summary>
    public void PlaySegment(string segId, string wavFilePath, long byteOffset, long durationMs)
    {
        if (!File.Exists(wavFilePath))
        {
            Console.WriteLine($"[AudioPlayerService] WAV file not found: {wavFilePath}");
            return;
        }

        lock (_lock)
        {
            StopCurrent();

            try
            {
                var reader = new WaveFileReader(wavFilePath);

                // Tính byte count tương ứng với durationMs
                // ByteRate = SampleRate * NumChannels * BitsPerSample / 8
                long bytesPerMs = reader.WaveFormat.AverageBytesPerSecond / 1000;
                long byteCount = bytesPerMs * durationMs;

                // Seek tới offset (tính từ đầu data chunk, không phải đầu file)
                // WAV header = 44 bytes, nên actual seek = 44 + byteOffset
                long seekPos = 44 + byteOffset;
                if (seekPos >= reader.Length)
                {
                    reader.Dispose();
                    return;
                }
                reader.Position = seekPos;

                // Wrap trong OffsetSampleProvider để giới hạn duration
                var provider = new LimitedWaveStream(reader, byteCount);
                _waveOut = new WaveOutEvent();
                _waveOut.Init(provider);
                _waveOut.PlaybackStopped += (s, e) =>
                {
                    reader.Dispose();
                    provider.Dispose();
                    PlaybackEnded?.Invoke(segId);
                };
                
                PlaybackStarted?.Invoke(segId);
                _waveOut.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Playback error: {ex.Message}");
                PlaybackEnded?.Invoke(segId);
            }
        }
    }

    public void StopCurrent()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
    }

    public void Dispose()
    {
        StopCurrent();
    }
}

/// <summary>
/// Stream wrapper giới hạn số bytes có thể đọc (dùng để cắt đoạn audio).
/// </summary>
public class LimitedWaveStream : WaveStream
{
    private readonly WaveStream _source;
    private readonly long _byteLimit;
    private readonly long _startOffset;
    private long _bytesRead;

    public LimitedWaveStream(WaveStream source, long byteLimit)
    {
        _source = source;
        _byteLimit = byteLimit;
        _startOffset = source.Position; // ✅ Lưu giữ offset xuất phát của segment
        _bytesRead = 0;
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => _byteLimit;
    public override long Position
    {
        get => _bytesRead;
        set 
        { 
            _bytesRead = Math.Clamp(value, 0, _byteLimit);
            _source.Position = _startOffset + _bytesRead; // ✅ Luôn định hướng từ điểm gốc segment
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _byteLimit - _bytesRead;
        if (remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, remaining);
        int read = _source.Read(buffer, offset, toRead);
        _bytesRead += read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        // Source được dispose bởi caller
        base.Dispose(disposing);
    }
}
