using System;
using System.IO;
using NAudio.Wave;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Phát một đoạn audio cụ thể từ file WAV hoặc session-based PCM chunks.
/// Thread-safe: cancel playback hiện tại trước khi phát mới.
/// </summary>
public class AudioPlayerService : IDisposable
{
    private WaveOutEvent? _waveOut;
    private readonly object _lock = new();

    public event Action<string>? PlaybackEnded; // segId
    public event Action<string>? PlaybackStarted; // segId

    /// <summary>
    /// Play segment from session-based PCM chunks (NEW API for Phase 2)
    /// </summary>
    public void PlaySegmentByTime(string segId, string sessionDir, long offsetMs, long durationMs)
    {
        System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] PlaySegmentByTime called:");
        System.Diagnostics.Debug.WriteLine($"  segId: {segId}");
        System.Diagnostics.Debug.WriteLine($"  sessionDir: {sessionDir}");
        System.Diagnostics.Debug.WriteLine($"  offsetMs: {offsetMs}");
        System.Diagnostics.Debug.WriteLine($"  durationMs: {durationMs}");
        
        if (!Directory.Exists(sessionDir))
        {
            System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Session directory not found: {sessionDir}");
            PlaybackEnded?.Invoke(segId);
            return;
        }

        lock (_lock)
        {
            StopCurrent();

            try
            {
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Loading VirtualWavReader from {sessionDir}");
                
                // Load virtual WAV reader from session chunks
                var reader = VirtualWavReader.FromSessionDir(sessionDir);
                
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Reader loaded. WaveFormat: {reader.WaveFormat}");
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Reader length: {reader.Length} bytes");
                
                // Seek to offset
                reader.SeekToMilliseconds(offsetMs);
                
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Seeked to {offsetMs}ms (position: {reader.Position})");
                
                // Calculate byte count for duration
                long bytesPerMs = reader.WaveFormat.AverageBytesPerSecond / 1000;
                long byteCount = bytesPerMs * durationMs;
                
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Will play {byteCount} bytes ({durationMs}ms)");
                
                // Wrap in limiter to play only duration
                var limiter = new LimitedWaveStream(reader, byteCount);
                
                _waveOut = new WaveOutEvent();
                _waveOut.Init(limiter);
                _waveOut.PlaybackStopped += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Playback stopped for {segId}");
                    reader.Dispose();
                    limiter.Dispose();
                    PlaybackEnded?.Invoke(segId);
                };
                
                PlaybackStarted?.Invoke(segId);
                _waveOut.Play();
                
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Playing segment {segId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] ❌ Playback error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[AudioPlayerService] Stack trace: {ex.StackTrace}");
                PlaybackEnded?.Invoke(segId);
            }
        }
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
    private long _bytesRead;

    public LimitedWaveStream(WaveStream source, long byteLimit)
    {
        _source = source;
        _byteLimit = byteLimit;
        _bytesRead = 0;
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;
    public override long Length => _byteLimit;
    public override long Position
    {
        get => _bytesRead;
        set { _source.Position = value; _bytesRead = value; }
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
