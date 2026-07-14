using System;
using Avalonia.Media;

namespace m_mslc_overlay.core
{
    /// <summary>
    /// Phase 4b (ATOM54) — Display representation of a segment with visual styling properties.
    /// Used by SegmentDisplayModel to maintain segment-aware render state.
    /// Supports visual differentiation between processing, confirmed, dangling, and stale segments.
    /// </summary>
    public class SegmentDisplayItem
    {
        /// <summary>
        /// Unique identifier linking to SegmentTracker.SegmentRecord.
        /// </summary>
        public Guid SegmentId { get; init; }

        /// <summary>
        /// Display text for this segment.
        /// VALIDATION: Must not be null (may be empty string).
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Current lifecycle state (Committed, Translated, Rendered, Stale).
        /// </summary>
        public SegmentState State { get; set; }

        /// <summary>
        /// Whether this segment has uncertain trailing words.
        /// When true, additional visual cues (underline, yellow color) are applied.
        /// </summary>
        public bool IsDangling { get; set; }

        /// <summary>
        /// Visual opacity for this segment.
        /// VALIDATION: Must be in range [0.0, 1.0].
        /// - 0.5 = processing (Committed state)
        /// - 1.0 = confirmed (Translated/Rendered state)
        /// - 0.3 = stale (no longer visible on overlay)
        /// </summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// Whether text should render in italic style.
        /// Used to indicate processing state (Committed).
        /// </summary>
        public bool IsItalic { get; set; }

        /// <summary>
        /// Whether text should have underline decoration.
        /// Used to indicate dangling segments with uncertain trailing words.
        /// </summary>
        public bool IsUnderlined { get; set; }

        /// <summary>
        /// Optional custom text color (null = use default foreground).
        /// VALIDATION: Must be a valid Avalonia IBrush instance or null.
        /// - Yellow (#FFAA00) for dangling segments
        /// - Gray (#808080) for stale segments
        /// </summary>
        public IBrush? TextColor { get; set; }

        /// <summary>
        /// Timestamp when segment was first created.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// Timestamp of last visual state update.
        /// </summary>
        public DateTime? LastUpdated { get; set; }

        /// <summary>
        /// Validates and clamps property values to ensure rendering safety.
        /// Called after setting properties to enforce constraints.
        /// </summary>
        public void ValidateAndClampProperties()
        {
            // Requirement 11.1: Opacity must be in range [0.0, 1.0]
            if (Opacity < 0.0) Opacity = 0.0;
            if (Opacity > 1.0) Opacity = 1.0;

            // Requirement 11.3: Text must not be null
            Text ??= string.Empty;

            // Requirement 11.2: TextColor must be valid IBrush or null
            // (No explicit validation needed - type system enforces this)
        }
    }
}
