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
        long? audioStartMs = null;
        long? audioEndMs = null;
        string? audioSessionId = null;
        
        if (_audioRecorder != null)
        {
            var audioRef = _audioRecorder.GetCurrentReference();
            audioSessionId = audioRef.sessionId;

            if (!_audioRecorder.HasAnchor)
            {
                _audioRecorder.SetFirstUtteranceAnchor(tsStartMs, tsEndMs);
            }

            long anchoredStart = _audioRecorder.AudioOffsetForTs(tsStartMs);
            long anchoredEnd = _audioRecorder.AudioOffsetForTs(tsEndMs);

            if (anchoredStart >= 0)
            {
                audioStartMs = anchoredStart;
                audioEndMs = anchoredEnd >= anchoredStart
                    ? anchoredEnd
                    : anchoredStart + Math.Max(0, tsEndMs - tsStartMs);
            }
            else
            {
                long currentRefMs = audioRef.offsetMs;
                long speechDurationMs = Math.Max(0, tsEndMs - tsStartMs);
                audioEndMs = currentRefMs;
                audioStartMs = Math.Max(0, currentRefMs - speechDurationMs);
            }
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
            AcousticEndMs = acousticEndMs,
            UtteranceOffset = utteranceOffset,
            IsDangling = isDangling,
            AvgSpeechSpeedMs = avgSpeechSpeedMs,
            CommitReason = commitReason,
            AudioSessionId = audioSessionId,
            AudioOffsetMs = audioStartMs,
            AudioEndOffsetMs = audioEndMs
        };

        var id = _activeRepo.InsertSegment(segment);
        segment.Id = id;

        if (_offsetIndex != null && segment.AudioOffsetMs.HasValue)
        {
            _offsetIndex.AppendOffset(id, segment.AudioOffsetMs.Value);
        }

        SegmentAdded?.Invoke(segment);
        return id;
    }
}
