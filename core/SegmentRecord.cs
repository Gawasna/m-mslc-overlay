using System;

namespace m_mslc_overlay.core
{
    /// <summary>
    /// ATOM79 — Immutable record for a single caption segment at a point in time.
    /// </summary>
    public class SegmentRecord
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        /// Source commit that created this segment.
        public CommitMetadata Commit { get; init; } = default!;

        /// Translation result, null until translation arrives.
        public TranslationResult? Translation { get; private set; }

        /// Current lifecycle state.
        public SegmentState State { get; private set; } = SegmentState.Committed;

        /// Wall-clock time when the segment was committed.
        public DateTime CommittedAt { get; init; } = DateTime.Now;

        /// Wall-clock time when translation arrived, null if not yet translated.
        public DateTime? TranslatedAt { get; private set; }

        /// Wall-clock time when the segment was rendered to overlay.
        public DateTime? RenderedAt { get; private set; }

        // Internal state transitions — only SegmentTracker should call these
        internal void SetTranslation(TranslationResult result)
        {
            Translation = result;
            TranslatedAt = DateTime.Now;
            State = SegmentState.Translated;
        }

        internal void MarkRendered()
        {
            RenderedAt = DateTime.Now;
            State = SegmentState.Rendered;
        }

        internal void MarkStale()
        {
            State = SegmentState.Stale;
        }

        /// Translation latency in milliseconds, or null if not yet translated.
        public double? TranslationLatencyMs => TranslatedAt.HasValue
            ? (TranslatedAt.Value - CommittedAt).TotalMilliseconds
            : null;
    }
}
