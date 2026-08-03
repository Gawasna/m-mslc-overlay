using System;
using System.Collections.Generic;

namespace MMslcOverlay.Core.Workspace.Models;

/// <summary>
/// Metadata cho một recording session, chứa thông tin về PCM chunks
/// </summary>
public class SessionMetadata
{
    public string SessionId { get; set; } = string.Empty;
    public long StartTime { get; set; }  // Unix timestamp ms
    public int SampleRate { get; set; } = 16000;
    public int BitsPerSample { get; set; } = 16;
    public int Channels { get; set; } = 1;
    public List<ChunkInfo> Chunks { get; set; } = new();
    public long TotalDurationMs { get; set; }
    public string Status { get; set; } = "recording";  // recording, completed, recovered
}

/// <summary>
/// Thông tin về một PCM chunk
/// </summary>
public class ChunkInfo
{
    public int ChunkId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public long StartOffsetMs { get; set; }  // Global offset trong session
    public string Status { get; set; } = "active";  // active, finalized, recovered
}
