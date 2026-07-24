using System;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;

namespace MMslcOverlay.Services.Workspace;

public class SegmentIngestionService
{
    private readonly BaseSegmentRepository _activeRepo;
    private readonly string _activeChunkId;

    public event Action<Segment>? SegmentAdded;

    public SegmentIngestionService(BaseSegmentRepository activeRepo, string activeChunkId)
    {
        _activeRepo = activeRepo;
        _activeChunkId = activeChunkId;
    }

    /// <summary>
    /// Nhận DTO từ hệ thống STT và lưu vào active.db
    /// </summary>
    public void IngestSttPayload(long tsStartMs, long tsEndMs, string textSrc, string? textTrs = null, string? speakerId = null, string commitType = "HARD")
    {
        var segment = new Segment
        {
            TsStartMs = tsStartMs,
            TsEndMs = tsEndMs,
            TextSrc = textSrc,
            TextTrs = textTrs,
            SpeakerId = speakerId,
            CommitType = commitType, 
            ChunkId = _activeChunkId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var id = _activeRepo.InsertSegment(segment);
        segment.Id = id;

        // Bắn sự kiện ra ngoài cho UI (Ví dụ: PaperSheetViewModel) update
        SegmentAdded?.Invoke(segment);
    }
}
