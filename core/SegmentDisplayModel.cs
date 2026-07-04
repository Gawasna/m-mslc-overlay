using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace m_mslc_overlay.core
{
    /// <summary>
    /// Phase 4b (ATOM54) — Segment-aware display state manager with thread-safe operations.
    /// Maintains an ordered list of segment display items with visual properties per segment.
    /// Supports concurrent updates from SegmentTracker events and UI rendering threads.
    /// 
    /// Requirements implemented:
    /// - 4.1: Maintain ordered list of segment display items with visual properties
    /// - 4.2: Store segment ID, text, state, visual properties, and timestamp on add
    /// - 8.1-8.5: Thread-safe access using lock object with read-only snapshots
    /// - 11.1-11.3: Property validation (opacity 0.0-1.0, non-null text, valid brush)
    /// </summary>
    public class SegmentDisplayModel
    {
        private readonly List<SegmentDisplayItem> _segments = new();
        private readonly object _lock = new();
        private readonly IBrush _yellowBrush = SolidColorBrush.Parse("#FFAA00");
        private readonly IBrush _grayBrush = SolidColorBrush.Parse("#808080");

        /// <summary>
        /// Adds a new segment to the display model from a SegmentRecord.
        /// Thread-safe operation using internal lock.
        /// 
        /// Validates: Requirement 11.3 (non-null text), 11.1 (opacity range)
        /// </summary>
        /// <param name="record">Source segment record with commit metadata</param>
        public void AddSegment(SegmentRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            // Create display item with initial visual properties based on state
            var displayItem = new SegmentDisplayItem
            {
                SegmentId = record.Id,
                Text = record.Commit.Text ?? string.Empty,
                State = record.State,
                IsDangling = record.Commit.IsDangling,
                CreatedAt = record.CommittedAt,
                LastUpdated = DateTime.Now
            };

            // Apply visual properties based on current state
            ApplyVisualPropertiesForState(displayItem);

            // Validate properties before adding
            displayItem.ValidateAndClampProperties();

            // Requirement 8.2: Acquire lock only during add operation
            lock (_lock)
            {
                _segments.Add(displayItem);
            }
        }

        /// <summary>
        /// Updates the visual state of an existing segment.
        /// Thread-safe operation using internal lock.
        /// 
        /// Requirement 8.4: Acquire lock only during update operation
        /// </summary>
        /// <param name="segmentId">Unique identifier of the segment to update</param>
        /// <param name="newState">New lifecycle state</param>
        /// <returns>True if segment was found and updated, false otherwise</returns>
        public bool UpdateSegmentState(Guid segmentId, SegmentState newState)
        {
            // Requirement 8.4: Acquire lock only during update
            lock (_lock)
            {
                var segment = _segments.FirstOrDefault(s => s.SegmentId == segmentId);
                if (segment == null)
                    return false;

                segment.State = newState;
                segment.LastUpdated = DateTime.Now;

                // Reapply visual properties based on new state
                ApplyVisualPropertiesForState(segment);
                segment.ValidateAndClampProperties();

                return true;
            }
        }

        /// <summary>
        /// Updates all visual properties of an existing segment.
        /// Thread-safe operation using internal lock.
        /// </summary>
        /// <param name="segmentId">Unique identifier of the segment to update</param>
        /// <param name="isDangling">Optional new dangling flag</param>
        /// <returns>True if segment was found and updated, false otherwise</returns>
        public bool UpdateSegmentProperties(Guid segmentId, bool? isDangling = null)
        {
            lock (_lock)
            {
                var segment = _segments.FirstOrDefault(s => s.SegmentId == segmentId);
                if (segment == null)
                    return false;

                if (isDangling.HasValue)
                    segment.IsDangling = isDangling.Value;

                segment.LastUpdated = DateTime.Now;

                // Reapply visual properties
                ApplyVisualPropertiesForState(segment);
                segment.ValidateAndClampProperties();

                return true;
            }
        }

        /// <summary>
        /// Removes a segment from the display model.
        /// Thread-safe operation using internal lock.
        /// 
        /// Requirement 8.2: Acquire lock only during remove operation
        /// </summary>
        /// <param name="segmentId">Unique identifier of the segment to remove</param>
        /// <returns>True if segment was found and removed, false otherwise</returns>
        public bool RemoveSegment(Guid segmentId)
        {
            // Requirement 8.2: Acquire lock only during remove
            lock (_lock)
            {
                var segment = _segments.FirstOrDefault(s => s.SegmentId == segmentId);
                if (segment == null)
                    return false;

                _segments.Remove(segment);
                return true;
            }
        }

        /// <summary>
        /// Retrieves all segments as a read-only snapshot.
        /// Thread-safe operation that returns a copy to avoid external mutation.
        /// 
        /// Requirement 8.3: Return read-only snapshot of segment list
        /// </summary>
        /// <returns>Read-only list of segment display items</returns>
        public IReadOnlyList<SegmentDisplayItem> GetAllSegments()
        {
            // Requirement 8.3: Return read-only snapshot
            lock (_lock)
            {
                // Create a shallow copy to provide snapshot semantics
                // The list itself is immutable from caller's perspective
                return _segments.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Clears all segments from the display model.
        /// Thread-safe operation using internal lock.
        /// </summary>
        public void ClearAllSegments()
        {
            lock (_lock)
            {
                _segments.Clear();
            }
        }

        /// <summary>
        /// Gets the current segment count.
        /// Thread-safe operation using internal lock.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _segments.Count;
                }
            }
        }

        /// <summary>
        /// Applies visual properties based on segment state and dangling flag.
        /// Implements the visual mapping rules from ATOM54 design.
        /// 
        /// Visual Mapping Rules:
        /// - Committed: opacity=0.5, italic=true
        /// - Committed + IsDangling: + underline, yellow color
        /// - Translated: opacity=1.0, italic=false
        /// - Translated + IsDangling: + underline, yellow color
        /// - Rendered: opacity=1.0, italic=false, no decorations
        /// - Stale: opacity=0.3, gray color
        /// 
        /// Requirement 11.4: Ensure SegmentState is valid enum value
        /// </summary>
        private void ApplyVisualPropertiesForState(SegmentDisplayItem item)
        {
            // Requirement 11.4: Validate SegmentState
            if (!Enum.IsDefined(typeof(SegmentState), item.State))
            {
                // Use default fallback: treat as Committed
                item.State = SegmentState.Committed;
            }

            switch (item.State)
            {
                case SegmentState.Committed:
                    // Requirement 3.1: Committed but not translated = 0.5 opacity + italic
                    item.Opacity = 0.5;
                    item.IsItalic = true;
                    item.IsUnderlined = item.IsDangling;
                    item.TextColor = item.IsDangling
                        ? _yellowBrush // Requirement 3.3: Yellow for dangling
                        : null;
                    break;

                case SegmentState.Translated:
                    // Requirement 3.2: Translated = 1.0 opacity + normal style
                    item.Opacity = 1.0;
                    item.IsItalic = false;
                    item.IsUnderlined = item.IsDangling;
                    item.TextColor = item.IsDangling
                        ? _yellowBrush
                        : null;
                    break;

                case SegmentState.Rendered:
                    // Final rendered state - full opacity, no decorations
                    item.Opacity = 1.0;
                    item.IsItalic = false;
                    item.IsUnderlined = false;
                    item.TextColor = null;
                    break;

                case SegmentState.Stale:
                    // Requirement 3.4: Stale = 0.3 opacity + gray color
                    item.Opacity = 0.3;
                    item.IsItalic = false;
                    item.IsUnderlined = false;
                    item.TextColor = _grayBrush; // Gray
                    break;

                default:
                    // Fallback to Committed state
                    item.Opacity = 0.5;
                    item.IsItalic = true;
                    item.IsUnderlined = false;
                    item.TextColor = null;
                    break;
            }
        }
    }
}
