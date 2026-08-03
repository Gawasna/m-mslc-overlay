using System;
using System.IO;
using System.Text.Json;
using MMslcOverlay.Core.Workspace.Models;
using NAudio.Wave;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Ghi audio streaming thành PCM chunks (crash-safe, append-only)
/// </summary>
public class StreamingPcmRecorder : IDisposable
{
    private readonly string _sessionDir;
    private readonly SessionMetadata _metadata;
    private FileStream? _currentChunkStream;
    private BinaryWriter? _currentChunkWriter;
    private WaveInEvent? _waveIn;
    private int _currentChunkId = 0;
    private long _currentChunkBytes = 0;
    private long _sessionOffsetMs = 0;
    private bool _isRecording;
    
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
            FileShare.Read,
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
        
        System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Started chunk {_currentChunkId}: {fileName}");
    }
    
    public void StartRecording()
    {
        if (_isRecording) return;
        
        try
        {
            _waveIn?.Dispose();
            
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(_metadata.SampleRate, _metadata.BitsPerSample, _metadata.Channels)
            };
            
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            
            _isRecording = true;
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Recording started for session {SessionId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamingPcmRecorder] Failed to start recording: {ex.Message}");
            throw;
        }
    }
    
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording || _currentChunkWriter == null) return;
        
        // Check if need to start new chunk
        if (_currentChunkBytes + e.BytesRecorded > MAX_CHUNK_SIZE_BYTES)
        {
            StartNewChunk();
        }
        
        // Write PCM data (append-only, immediate flush)
        _currentChunkWriter.Write(e.Buffer, 0, e.BytesRecorded);
        _currentChunkWriter.Flush();
        _currentChunkStream?.Flush(flushToDisk: true);  // Force physical write
        
        _currentChunkBytes += e.BytesRecorded;
        _sessionOffsetMs += e.BytesRecorded / BYTES_PER_MS;
    }
    
    public void StopRecording()
    {
        if (!_isRecording) return;
        
        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
            
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
    
    public void Dispose()
    {
        StopRecording();
    }
}
