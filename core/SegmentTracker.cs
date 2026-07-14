using System;
using System.Collections.Generic;
using System.Linq;

namespace m_mslc_overlay.core
{
    /// <summary>
    /// ATOM79 — Tracks the lifecycle of caption segments across the pipeline.
    ///
    /// This is a Phase 2 infrastructure service. It observes commits and translations
    /// without changing overlay render behavior. Phase 3 (ATOM80 RevisionWindow,
    /// ATOM76 Speculative hot-replace) will use events fired here.
    ///
    /// Segments are keyed by UtteranceOffset for translation linking.
    /// The recent segment window is capped at MaxSegmentHistory to avoid unbounded growth.
    /// </summary>
    public class SegmentTracker
    {
        public const int MaxSegmentHistory = 20;

        private readonly object _lock = new();

        // Active segments indexed by UtteranceOffset for O(1) translation linking
        private readonly Dictionary<ulong, List<SegmentRecord>> _byOffset = new();

        // Ordered list of all segments (newest last) for history/debug
        private readonly List<SegmentRecord> _history = new();

        // ── Events for Phase 3 consumers ──────────────────────────────────────
        /// Fired when a new segment is committed.
        public event Action<SegmentRecord>? OnSegmentCommitted;

        /// Fired when a translation is linked to a segment.
        public event Action<SegmentRecord>? OnSegmentTranslated;

        /// Fired when a segment is marked as rendered.
        public event Action<SegmentRecord>? OnSegmentRendered;

        /// Fired when segments are marked stale (overlay reset).
        public event Action<IReadOnlyList<SegmentRecord>>? OnSegmentsStale;

        // ── Public API ────────────────────────────────────────────────────────

        /// Called when a commit arrives from the engine pipeline.
        public SegmentRecord TrackCommit(CommitMetadata meta)
        {
            var record = new SegmentRecord { Commit = meta };

            lock (_lock)
            {
                // Index by UtteranceOffset for translation linking
                if (!_byOffset.TryGetValue(meta.UtteranceOffset, out var bucket))
                {
                    bucket = new List<SegmentRecord>();
                    _byOffset[meta.UtteranceOffset] = bucket;
                }
                bucket.Add(record);

                _history.Add(record);
                TrimHistory();
            }

            OnSegmentCommitted?.Invoke(record);
            return record;
        }

        /// Called when a TranslationResult arrives from AIService.
        /// Links the result to the most recent unlinked segment with matching UtteranceOffset.
        /// Falls back to the most recently committed unlinked segment if offset is 0 or not found.
        public SegmentRecord? LinkTranslation(TranslationResult result)
        {
            if (result.Source == null) return null;

            SegmentRecord? target = null;

            lock (_lock)
            {
                ulong offset = result.Source.UtteranceOffset;

                // Try to find matching unlinked segment by offset
                if (offset != 0 && _byOffset.TryGetValue(offset, out var bucket))
                {
                    target = bucket.LastOrDefault(r =>
                        r.State == SegmentState.Committed &&
                        r.Commit.Text == result.Source.Text);

                    // Fallback: any committed segment in same utterance
                    target ??= bucket.LastOrDefault(r => r.State == SegmentState.Committed);
                }

                // Final fallback: most recently committed segment overall
                target ??= _history.LastOrDefault(r => r.State == SegmentState.Committed);

                target?.SetTranslation(result);
            }

            if (target != null)
                OnSegmentTranslated?.Invoke(target);

            return target;
        }

        /// Called after text is sent to the overlay for rendering.
        public void MarkRendered(SegmentRecord record)
        {
            lock (_lock) { record.MarkRendered(); }
            OnSegmentRendered?.Invoke(record);
        }

        /// Called when the overlay is cleared/reset (ATOM75 constraint).
        /// Marks all currently Committed/Translated segments as Stale.
        public void MarkOverlayReset()
        {
            List<SegmentRecord> stale;
            lock (_lock)
            {
                stale = _history
                    .Where(r => r.State == SegmentState.Committed || r.State == SegmentState.Translated)
                    .ToList();
                foreach (var r in stale) r.MarkStale();
            }

            if (stale.Count > 0)
                OnSegmentsStale?.Invoke(stale);
        }

        /// Returns a snapshot of recent segment history for debug/logging.
        public IReadOnlyList<SegmentRecord> GetRecentHistory(int count = 10)
        {
            lock (_lock)
            {
                int start = Math.Max(0, _history.Count - count);
                return _history.Skip(start).ToList();
            }
        }

        /// Reset all state (e.g. on pipe reconnect).
        public void Reset()
        {
            lock (_lock)
            {
                _byOffset.Clear();
                _history.Clear();
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void TrimHistory()
        {
            while (_history.Count > MaxSegmentHistory)
            {
                var oldest = _history[0];
                _history.RemoveAt(0);

                // Clean up offset index for oldest entry
                if (_byOffset.TryGetValue(oldest.Commit.UtteranceOffset, out var bucket))
                {
                    bucket.Remove(oldest);
                    if (bucket.Count == 0)
                        _byOffset.Remove(oldest.Commit.UtteranceOffset);
                }
            }
        }
    }
}
