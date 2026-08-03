using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Phục hồi audio sessions bị incomplete do crash hoặc force quit.
/// Quét orphaned chunks và rebuild metadata.json.
/// </summary>
public class SessionRecoveryService
{
    private readonly string _audioDir;

    public SessionRecoveryService(string audioDir)
    {
        _audioDir = audioDir;
    }

    /// <summary>
    /// Quét tất cả sessions trong audioDir và recover những session incomplete.
    /// Returns: List of recovered session IDs
    /// </summary>
    public List<string> RecoverAllSessions()
    {
        var recoveredSessions = new List<string>();

        if (!Directory.Exists(_audioDir))
        {
            System.Diagnostics.Debug.WriteLine($"[SessionRecovery] Audio directory not found: {_audioDir}");
            return recoveredSessions;
        }

        var sessionDirs = Directory.GetDirectories(_audioDir, "session_*");

        foreach (var sessionDir in sessionDirs)
        {
            string sessionId = Path.GetFileName(sessionDir);
            
            try
            {
                bool recovered = RecoverSession(sessionDir);
                if (recovered)
                {
                    recoveredSessions.Add(sessionId);
                    System.Diagnostics.Debug.WriteLine($"[SessionRecovery] ✅ Recovered session: {sessionId}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionRecovery] ❌ Failed to recover {sessionId}: {ex.Message}");
            }
        }

        return recoveredSessions;
    }

    /// <summary>
    /// Recover một session cụ thể. Returns true nếu có orphaned chunks được phục hồi.
    /// </summary>
    public bool RecoverSession(string sessionDir)
    {
        string metadataPath = Path.Combine(sessionDir, "metadata.json");
        
        // Load existing metadata (or create new if missing)
        SessionMetadata metadata;
        
        if (File.Exists(metadataPath))
        {
            try
            {
                string json = File.ReadAllText(metadataPath);
                metadata = JsonSerializer.Deserialize<SessionMetadata>(json) 
                    ?? CreateEmptyMetadata(sessionDir);
            }
            catch (JsonException)
            {
                // Corrupt metadata, rebuild from scratch
                System.Diagnostics.Debug.WriteLine($"[SessionRecovery] Corrupt metadata.json, rebuilding from scratch");
                metadata = CreateEmptyMetadata(sessionDir);
            }
        }
        else
        {
            // Missing metadata, create new
            System.Diagnostics.Debug.WriteLine($"[SessionRecovery] Missing metadata.json, creating from chunks");
            metadata = CreateEmptyMetadata(sessionDir);
        }

        // Scan for all PCM chunks
        var allChunkFiles = Directory.GetFiles(sessionDir, "chunk_*.pcm")
            .OrderBy(f => f)
            .ToList();

        if (allChunkFiles.Count == 0)
        {
            // No chunks to recover
            return false;
        }

        // Get registered chunks
        var registeredFiles = metadata.Chunks
            .Select(c => Path.Combine(sessionDir, c.FileName))
            .ToHashSet();

        // Find orphaned chunks
        var orphanedChunks = allChunkFiles
            .Where(f => !registeredFiles.Contains(f))
            .ToList();

        if (orphanedChunks.Count == 0)
        {
            // No orphaned chunks, nothing to recover
            return false;
        }

        // Recover orphaned chunks
        System.Diagnostics.Debug.WriteLine($"[SessionRecovery] Found {orphanedChunks.Count} orphaned chunks");

        foreach (var chunkPath in orphanedChunks)
        {
            string fileName = Path.GetFileName(chunkPath);
            int chunkId = ParseChunkId(fileName);
            
            var fileInfo = new FileInfo(chunkPath);
            long sizeBytes = fileInfo.Length;
            
            // Calculate duration: 16kHz, 16-bit, Mono = 32 bytes/ms
            const long BYTES_PER_MS = 32;
            long durationMs = sizeBytes / BYTES_PER_MS;
            
            // Calculate start offset (sum of all previous chunks)
            long startOffsetMs = metadata.Chunks
                .Where(c => c.ChunkId < chunkId)
                .Sum(c => c.DurationMs);

            var chunkInfo = new ChunkInfo
            {
                ChunkId = chunkId,
                FileName = fileName,
                SizeBytes = sizeBytes,
                DurationMs = durationMs,
                StartOffsetMs = startOffsetMs,
                Status = "recovered"  // Mark as recovered
            };

            metadata.Chunks.Add(chunkInfo);
            
            System.Diagnostics.Debug.WriteLine(
                $"[SessionRecovery]   Recovered chunk {chunkId}: {fileName} ({sizeBytes:N0} bytes, {durationMs}ms)");
        }

        // Sort chunks by ID
        metadata.Chunks = metadata.Chunks.OrderBy(c => c.ChunkId).ToList();

        // Recalculate total duration
        metadata.TotalDurationMs = metadata.Chunks.Sum(c => c.DurationMs);

        // Mark session as recovered
        if (metadata.Status == "recording")
        {
            metadata.Status = "recovered";
        }

        // Save updated metadata
        SaveMetadata(sessionDir, metadata);

        return true;
    }

    /// <summary>
    /// Parse chunk ID from filename: "chunk_003.pcm" → 3
    /// </summary>
    private int ParseChunkId(string fileName)
    {
        // Extract number from "chunk_NNN.pcm"
        string numPart = fileName.Substring(6, 3);  // "chunk_003.pcm" → "003"
        return int.Parse(numPart);
    }

    /// <summary>
    /// Create empty metadata từ session directory name
    /// </summary>
    private SessionMetadata CreateEmptyMetadata(string sessionDir)
    {
        string sessionId = Path.GetFileName(sessionDir);
        
        return new SessionMetadata
        {
            SessionId = sessionId,
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SampleRate = 16000,
            BitsPerSample = 16,
            Channels = 1,
            Chunks = new List<ChunkInfo>(),
            TotalDurationMs = 0,
            Status = "unknown"
        };
    }

    /// <summary>
    /// Save metadata to file
    /// </summary>
    private void SaveMetadata(string sessionDir, SessionMetadata metadata)
    {
        try
        {
            string metadataPath = Path.Combine(sessionDir, "metadata.json");
            string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            File.WriteAllText(metadataPath, json);
            
            System.Diagnostics.Debug.WriteLine($"[SessionRecovery] Saved metadata: {metadataPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionRecovery] Failed to save metadata: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Validate session integrity (checks if all chunks exist and metadata is consistent)
    /// </summary>
    public bool ValidateSession(string sessionDir)
    {
        string metadataPath = Path.Combine(sessionDir, "metadata.json");
        
        if (!File.Exists(metadataPath))
            return false;

        try
        {
            string json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<SessionMetadata>(json);
            
            if (metadata == null || metadata.Chunks.Count == 0)
                return false;

            // Check all chunks exist
            foreach (var chunk in metadata.Chunks)
            {
                string chunkPath = Path.Combine(sessionDir, chunk.FileName);
                if (!File.Exists(chunkPath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SessionRecovery] Missing chunk file: {chunk.FileName}");
                    return false;
                }

                // Verify chunk size matches
                var fileInfo = new FileInfo(chunkPath);
                if (fileInfo.Length != chunk.SizeBytes)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SessionRecovery] Chunk size mismatch: {chunk.FileName} " +
                        $"(expected {chunk.SizeBytes}, got {fileInfo.Length})");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionRecovery] Validation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get session statistics
    /// </summary>
    public SessionStats? GetSessionStats(string sessionDir)
    {
        string metadataPath = Path.Combine(sessionDir, "metadata.json");
        
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            string json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<SessionMetadata>(json);
            
            if (metadata == null)
                return null;

            long totalBytes = metadata.Chunks.Sum(c => c.SizeBytes);
            int recoveredCount = metadata.Chunks.Count(c => c.Status == "recovered");

            return new SessionStats
            {
                SessionId = metadata.SessionId,
                ChunkCount = metadata.Chunks.Count,
                TotalDurationMs = metadata.TotalDurationMs,
                TotalSizeBytes = totalBytes,
                Status = metadata.Status,
                RecoveredChunkCount = recoveredCount,
                IsValid = ValidateSession(sessionDir)
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Session statistics
/// </summary>
public class SessionStats
{
    public string SessionId { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public long TotalDurationMs { get; set; }
    public long TotalSizeBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public int RecoveredChunkCount { get; set; }
    public bool IsValid { get; set; }

    public string DurationFormatted => 
        TimeSpan.FromMilliseconds(TotalDurationMs).ToString(@"hh\:mm\:ss");
    
    public string SizeFormatted => 
        TotalSizeBytes < 1024 * 1024 
            ? $"{TotalSizeBytes / 1024.0:F1} KB"
            : $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB";
}
