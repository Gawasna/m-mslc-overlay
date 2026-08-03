using System;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;
using MMslcOverlay.Core.Workspace.Storage;

namespace MMslcOverlay.Services.Workspace;

public class SegmentIngestionService
{
    private readonly BaseSegmentRepository _activeRepo;
    private readonly string _activeChunkId;
    private readonly StreamingPcmRecorder? _audioRecorder;
    private readonly AudioOffsetIndex? _offsetIndex;

    public event Action<Segment>? SegmentAdded;

    public SegmentIngestionService(
        BaseSegmentRepository activeRepo, 
        string activeChunkId,
        StreamingPcmRecorder? audioRecorder = null,
        AudioOffsetIndex? offsetIndex = null)
    {
        _activeRepo = activeRepo;
        _activeChunkId = activeChunkId;
        _audioRecorder = audioRecorder;
        _offsetIndex = offsetIndex;
    }

    /// <summary>
    /// Nhận DTO từ hệ thống STT và lưu vào active.db
    /// </summary>
    public long IngestSttPayload(
        long tsStartMs, 
        long tsEndMs, 
        string textSrc, 
        string? textTrs = null, 
        string? speakerId = null, 
        string commitType = "HARD",
        // CRITICAL-TEXT-001: Acoustic metadata from AdaptiveCommitEngine
        double? acousticEndMs = null,
        ulong? utteranceOffset = null,
        bool isDangling = false,
        int? avgSpeechSpeedMs = null,
        string commitReason = "UNKNOWN")
    {
        // ✅ FIX: Capture start offset TRƯỚC khi tạo segment object
        // AudioOffsetMs = vị trí recorder TẠI THỜI ĐIỂM commit text này
        // Đây chính xác là điểm âm thanh tương ứng với đoạn văn bản
        long? audioStartMs = null;
        long? audioEndMs = null;
        string? audioSessionId = null;
        
        if (_audioRecorder != null)
        {
            var audioRef = _audioRecorder.GetCurrentReference();
            audioSessionId = audioRef.sessionId;
            audioStartMs = audioRef.offsetMs;
            
            System.Diagnostics.Debug.WriteLine(
                $"[SegmentIngestion] Audio START ref: session={audioRef.sessionId}, startOffset={audioRef.offsetMs}ms");
        }

        var segment = new Segment
        {
            TsStartMs = tsStartMs,
            TsEndMs = tsEndMs,
            TextSrc = textSrc,
            TextTrs = textTrs,
            SpeakerId = speakerId,
            CommitType = commitType, 
            ChunkId = _activeChunkId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            
            // CRITICAL-TEXT-001: Store acoustic metadata for debugging and accuracy
            AcousticEndMs = acousticEndMs,
            UtteranceOffset = utteranceOffset,
            IsDangling = isDangling,
            AvgSpeechSpeedMs = avgSpeechSpeedMs,
            CommitReason = commitReason,
            
            // Audio reference: start offset
            AudioSessionId = audioSessionId,
            AudioOffsetMs = audioStartMs
        };

        var id = _activeRepo.InsertSegment(segment);
        segment.Id = id;

        // ✅ FIX: Capture end offset SAU khi insert (recorder vẫn đang chạy)
        // Đây là thời điểm segment được commit, audio tiếp theo bắt đầu từ đây
        if (_audioRecorder != null)
        {
            var endRef = _audioRecorder.GetCurrentReference();
            audioEndMs = endRef.offsetMs;
            segment.AudioEndOffsetMs = audioEndMs;
            
            System.Diagnostics.Debug.WriteLine(
                $"[SegmentIngestion] Audio END ref: session={endRef.sessionId}, endOffset={endRef.offsetMs}ms, " +
                $"duration={(audioEndMs - audioStartMs)}ms");
        }

        // ✅ Dual write to offset backup file (dùng start offset)
        if (_offsetIndex != null && segment.AudioOffsetMs.HasValue)
        {
            _offsetIndex.AppendOffset(id, segment.AudioOffsetMs.Value);
        }

        // Bắn sự kiện ra ngoài cho UI (Ví dụ: PaperSheetViewModel) update
        SegmentAdded?.Invoke(segment);
        return id;
    }
}
