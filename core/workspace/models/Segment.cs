using System;

namespace MMslcOverlay.Core.Workspace.Models;

public class Segment
{
    // Cột ID trong SQLite, tự động tăng
    public long Id { get; set; }
    
    // Timecode bắt đầu tuyệt đối (ms kể từ đầu phiên)
    public long TsStartMs { get; set; }
    
    // Timecode kết thúc
    public long TsEndMs { get; set; }
    
    // "SPK_1", "SPK_2", ...
    public string? SpeakerId { get; set; }
    
    // Văn bản gốc (từ STT)
    public string TextSrc { get; set; } = string.Empty;
    
    // Bản dịch (nullable: chưa dịch xong)
    public string? TextTrs { get; set; }
    
    // "HARD" | "SOFT" | "PARTIAL"
    public string CommitType { get; set; } = "HARD";
    
    // ASR rollback: trỏ về record bị thay thế
    public long? SupersedesId { get; set; }
    
    // "seg_001" | "seg_002" | "active" (để cross-chunk reference)
    public string ChunkId { get; set; } = "active";
    
    // Unix timestamp ms
    public long CreatedAt { get; set; }
    
    // ────────────────────────────────────────────────────────────────────
    // CRITICAL-TEXT-001: Acoustic metadata from AdaptiveCommitEngine
    // ────────────────────────────────────────────────────────────────────
    
    /// <summary>
    /// Acoustic end timestamp from SDK (in milliseconds).
    /// More precise than TsEndMs which may be wall-clock based.
    /// </summary>
    public double? AcousticEndMs { get; set; }
    
    /// <summary>
    /// SDK utterance offset (in 100ns ticks).
    /// Used for translation linking and debugging.
    /// </summary>
    public ulong? UtteranceOffset { get; set; }
    
    /// <summary>
    /// ATOM77: Flag indicating last word is dangling (incomplete phrase).
    /// Example: "how much" without complement.
    /// </summary>
    public bool IsDangling { get; set; }
    
    /// <summary>
    /// Average speech speed at commit time (ms per word).
    /// Used for pacing analysis and debugging.
    /// </summary>
    public int? AvgSpeechSpeedMs { get; set; }
    
    /// <summary>
    /// Commit reason from AdaptiveCommitEngine.
    /// Values: "HardCommit", "SoftCommit", "DebounceCommit", "OffsetChange"
    /// </summary>
    public string CommitReason { get; set; } = "UNKNOWN";
}
