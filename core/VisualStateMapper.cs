using System;
using System.Collections.Generic;

namespace m_mslc_overlay.core
{
    public class VisualStateMapper : IDisposable
    {
        private readonly SegmentDisplayModel _model;
        private readonly SegmentTracker _tracker;

        public VisualStateMapper(SegmentDisplayModel model, SegmentTracker tracker)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

            _tracker.OnSegmentCommitted += HandleCommitted;
            _tracker.OnSegmentTranslated += HandleTranslated;
            _tracker.OnSegmentRendered += HandleRendered;
            _tracker.OnSegmentsStale += HandleStale;
        }

        private void HandleCommitted(SegmentRecord record)
        {
            _model.AddSegment(record);
            m_mslc_overlay.services.LoggerService.Log($"[VisualStateMapper] Committed {record.Id.ToString().Substring(0, 8)} | Dangling: {record.Commit?.IsDangling} | Opacity: 0.5 | Italic: True");
            if (record.Commit?.IsDangling == true)
                m_mslc_overlay.services.LoggerService.Log($"[VisualStateMapper] Segment {record.Id.ToString().Substring(0, 8)} styled with Underline + Yellow color (Dangling)");
        }

        private void HandleTranslated(SegmentRecord record)
        {
            _model.UpdateSegmentState(record.Id, SegmentState.Translated);
            m_mslc_overlay.services.LoggerService.Log($"[VisualStateMapper] Translated {record.Id.ToString().Substring(0, 8)} | Opacity: 0.5 -> 1.0 | Italic: False");
            if (record.Commit != null)
            {
                _model.UpdateSegmentProperties(record.Id, record.Commit.IsDangling);
                if (record.Commit.IsDangling)
                    m_mslc_overlay.services.LoggerService.Log($"[VisualStateMapper] Segment {record.Id.ToString().Substring(0, 8)} styled with Underline + Yellow color (Dangling)");
            }
        }

        private void HandleRendered(SegmentRecord record)
        {
            _model.UpdateSegmentState(record.Id, SegmentState.Rendered);
            m_mslc_overlay.services.LoggerService.Log($"[VisualStateMapper] Rendered {record.Id.ToString().Substring(0, 8)} | Clean styling (No decorations)");
        }

        private void HandleStale(IReadOnlyList<SegmentRecord> records)
        {
            foreach (var r in records)
            {
                _model.UpdateSegmentState(r.Id, SegmentState.Stale);
                m_mslc_overlay.services.LoggerService.Log($"[VisualStateMapper] Stale {r.Id.ToString().Substring(0, 8)} | Opacity -> 0.3 | Color -> Gray");
            }
        }

        public void Dispose()
        {
            _tracker.OnSegmentCommitted -= HandleCommitted;
            _tracker.OnSegmentTranslated -= HandleTranslated;
            _tracker.OnSegmentRendered -= HandleRendered;
            _tracker.OnSegmentsStale -= HandleStale;
        }
    }
}
