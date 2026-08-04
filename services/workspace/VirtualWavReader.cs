using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MMslcOverlay.Core.Workspace.Models;
using NAudio.Wave;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Virtual WAV stream that concatenates multiple PCM chunks on-the-fly.
/// Implements WaveStream interface for NAudio playback.
/// </summary>
public class VirtualWavReader : WaveStream
{
    private readonly List<ChunkInfo> _chunks;
    private readonly string _sessionDir;
    private readonly WaveFormat _format;
    private int _currentChunkIndex = 0;
    private FileStream? _currentStream;
    private long _totalLength;
    private long _position = 0;
    
    public VirtualWavReader(string sessionDir, SessionMetadata metadata)
    {
        _sessionDir = sessionDir;
        _chunks = metadata.Chunks.OrderBy(c => c.StartOffsetMs).ToList();
        _format = new WaveFormat(metadata.SampleRate, metadata.BitsPerSample, metadata.Channels);
        _totalLength = _chunks.Sum(c => c.SizeBytes);
        
        if (_chunks.Count == 0)
            throw new InvalidOperationException("Session has no audio chunks");
    }
    
    /// <summary>
    /// Load VirtualWavReader from session directory (reads metadata.json)
    /// </summary>
    public static VirtualWavReader FromSessionDir(string sessionDir)
    {
        string metadataPath = Path.Combine(sessionDir, "metadata.json");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException($"metadata.json not found in {sessionDir}");
        
        string json = File.ReadAllText(metadataPath);
        var metadata = JsonSerializer.Deserialize<SessionMetadata>(json);
        if (metadata == null)
            throw new InvalidDataException("Failed to deserialize metadata.json");
        
        // Check actual disk file size in case session is actively being recorded
        foreach (var chunk in metadata.Chunks)
        {
            string chunkPath = Path.Combine(sessionDir, chunk.FileName);
            if (File.Exists(chunkPath))
            {
                var fi = new FileInfo(chunkPath);
                if (fi.Length > chunk.SizeBytes)
                {
                    chunk.SizeBytes = fi.Length;
                }
            }
        }
        
        return new VirtualWavReader(sessionDir, metadata);
    }
    
    public override WaveFormat WaveFormat => _format;
    public override long Length => _totalLength;
    
    public override long Position
    {
        get => _position;
        set
        {
            // Seek to position across chunks
            long remainingBytes = value;
            
            for (int i = 0; i < _chunks.Count; i++)
            {
                if (remainingBytes < _chunks[i].SizeBytes)
                {
                    // Target position is in this chunk
                    OpenChunk(i);
                    _currentStream?.Seek(remainingBytes, SeekOrigin.Begin);
                    _position = value;
                    return;
                }
                remainingBytes -= _chunks[i].SizeBytes;
            }
            
            // Beyond end of stream
            if (_currentStream != null)
            {
                _currentStream.Dispose();
                _currentStream = null;
            }
            _currentChunkIndex = _chunks.Count;
            _position = _totalLength;
        }
    }
    
    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        
        while (count > 0 && _currentChunkIndex < _chunks.Count)
        {
            if (_currentStream == null)
            {
                OpenChunk(_currentChunkIndex);
                if (_currentStream == null) break;
            }
            
            int read = _currentStream!.Read(buffer, offset, count);
            
            if (read == 0)
            {
                // End of current chunk, move to next
                _currentStream.Dispose();
                _currentStream = null;
                _currentChunkIndex++;
                continue;
            }
            
            totalRead += read;
            offset += read;
            count -= read;
            _position += read;
        }
        
        return totalRead;
    }
    
    /// <summary>
    /// Seek to a specific offset in milliseconds (converts to byte position)
    /// </summary>
    public void SeekToMilliseconds(long offsetMs)
    {
        long bytesPerMs = _format.AverageBytesPerSecond / 1000;
        long byteOffset = offsetMs * bytesPerMs;
        Position = byteOffset;
    }
    
    private void OpenChunk(int index)
    {
        _currentStream?.Dispose();
        _currentChunkIndex = index;
        
        string path = Path.Combine(_sessionDir, _chunks[index].FileName);
        
        if (!File.Exists(path))
        {
            Console.WriteLine($"[VirtualWavReader] ❌ Chunk file not found: {path}");
            System.Diagnostics.Debug.WriteLine($"[VirtualWavReader] Chunk file not found: {path}");
            _currentStream = null;
            return;
        }
        
        try
        {
            _currentStream = new FileStream(
                path, 
                FileMode.Open, 
                FileAccess.Read, 
                FileShare.ReadWrite,
                bufferSize: 8192
            );
            
            System.Diagnostics.Debug.WriteLine($"[VirtualWavReader] Opened chunk {index + 1}/{_chunks.Count}: {_chunks[index].FileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VirtualWavReader] ❌ Error opening chunk '{path}': {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[VirtualWavReader] Error opening chunk: {ex.Message}");
            _currentStream = null;
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _currentStream?.Dispose();
            _currentStream = null;
        }
        base.Dispose(disposing);
    }
}
