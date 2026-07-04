using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Xunit;

namespace m_mslc_overlay.core.tests
{
    /// <summary>
    /// Unit tests for SegmentDisplayItem and SegmentDisplayModel classes.
    /// Tests cover requirements 4.1-4.5, 8.1-8.5, 11.1-11.3.
    /// </summary>
    public class SegmentDisplayModelTests
    {
        #region SegmentDisplayItem Tests

        [Fact]
        public void SegmentDisplayItem_ValidateAndClampProperties_ClampsOpacityToValidRange()
        {
            // Requirement 11.1: Opacity must be clamped to [0.0, 1.0]
            var item = new SegmentDisplayItem
            {
                SegmentId = Guid.NewGuid(),
                Opacity = 1.5,
                CreatedAt = DateTime.Now
            };

            item.ValidateAndClampProperties();

            Assert.Equal(1.0, item.Opacity);
        }

        [Fact]
        public void SegmentDisplayItem_ValidateAndClampProperties_ClampsNegativeOpacity()
        {
            // Requirement 11.1: Opacity must be clamped to [0.0, 1.0]
            var item = new SegmentDisplayItem
            {
                SegmentId = Guid.NewGuid(),
                Opacity = -0.5,
                CreatedAt = DateTime.Now
            };

            item.ValidateAndClampProperties();

            Assert.Equal(0.0, item.Opacity);
        }

        [Fact]
        public void SegmentDisplayItem_ValidateAndClampProperties_EnsuresTextNotNull()
        {
            // Requirement 11.3: Text must not be null
            var item = new SegmentDisplayItem
            {
                SegmentId = Guid.NewGuid(),
                Text = null!,
                CreatedAt = DateTime.Now
            };

            item.ValidateAndClampProperties();

            Assert.NotNull(item.Text);
            Assert.Equal(string.Empty, item.Text);
        }

        [Fact]
        public void SegmentDisplayItem_TextColor_AcceptsValidBrush()
        {
            // Requirement 11.2: TextColor must be valid IBrush or null
            var item = new SegmentDisplayItem
            {
                SegmentId = Guid.NewGuid(),
                TextColor = SolidColorBrush.Parse("#FFAA00"),
                CreatedAt = DateTime.Now
            };

            Assert.NotNull(item.TextColor);
            Assert.IsAssignableFrom<IBrush>(item.TextColor);
        }

        [Fact]
        public void SegmentDisplayItem_TextColor_AcceptsNull()
        {
            // Requirement 11.2: TextColor can be null for default foreground
            var item = new SegmentDisplayItem
            {
                SegmentId = Guid.NewGuid(),
                TextColor = null,
                CreatedAt = DateTime.Now
            };

            Assert.Null(item.TextColor);
        }

        #endregion

        #region SegmentDisplayModel Tests

        [Fact]
        public void SegmentDisplayModel_AddSegment_StoresSegmentWithProperties()
        {
            // Requirement 4.2: Store segment ID, text, state, visual properties, timestamp
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test text", "HardCommit", isDangling: false);
            var record = new SegmentRecord
            {
                Commit = commit,
                CommittedAt = DateTime.Now
            };

            model.AddSegment(record);

            var segments = model.GetAllSegments();
            Assert.Single(segments);
            Assert.Equal(record.Id, segments[0].SegmentId);
            Assert.Equal("Test text", segments[0].Text);
            Assert.Equal(SegmentState.Committed, segments[0].State);
        }

        [Fact]
        public void SegmentDisplayModel_AddSegment_AppliesCommittedStateVisualProperties()
        {
            // Requirement 3.1: Committed state = 0.5 opacity + italic
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit");
            var record = new SegmentRecord { Commit = commit };

            model.AddSegment(record);

            var segment = model.GetAllSegments()[0];
            Assert.Equal(0.5, segment.Opacity);
            Assert.True(segment.IsItalic);
            Assert.False(segment.IsUnderlined);
        }

        [Fact]
        public void SegmentDisplayModel_AddSegment_AppliesDanglingVisualCues()
        {
            // Requirement 3.3: Dangling segments = underline + yellow color
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit", isDangling: true);
            var record = new SegmentRecord { Commit = commit };

            model.AddSegment(record);

            var segment = model.GetAllSegments()[0];
            Assert.True(segment.IsUnderlined);
            Assert.NotNull(segment.TextColor);
        }

        [Fact]
        public void SegmentDisplayModel_UpdateSegmentState_UpdatesStateAndVisualProperties()
        {
            // Requirement 8.4: Update segment state with lock
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit");
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            var segmentId = model.GetAllSegments()[0].SegmentId;
            bool updated = model.UpdateSegmentState(segmentId, SegmentState.Translated);

            Assert.True(updated);
            var segment = model.GetAllSegments()[0];
            Assert.Equal(SegmentState.Translated, segment.State);
            Assert.Equal(1.0, segment.Opacity); // Requirement 3.2: Translated = 1.0 opacity
            Assert.False(segment.IsItalic);
        }

        [Fact]
        public void SegmentDisplayModel_UpdateSegmentState_ReturnsFalseForNonexistentSegment()
        {
            var model = new SegmentDisplayModel();
            bool updated = model.UpdateSegmentState(Guid.NewGuid(), SegmentState.Translated);

            Assert.False(updated);
        }

        [Fact]
        public void SegmentDisplayModel_RemoveSegment_RemovesSegmentFromList()
        {
            // Requirement 8.2: Remove with lock
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit");
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            var segmentId = model.GetAllSegments()[0].SegmentId;
            bool removed = model.RemoveSegment(segmentId);

            Assert.True(removed);
            Assert.Empty(model.GetAllSegments());
        }

        [Fact]
        public void SegmentDisplayModel_RemoveSegment_ReturnsFalseForNonexistentSegment()
        {
            var model = new SegmentDisplayModel();
            bool removed = model.RemoveSegment(Guid.NewGuid());

            Assert.False(removed);
        }

        [Fact]
        public void SegmentDisplayModel_GetAllSegments_ReturnsReadOnlySnapshot()
        {
            // Requirement 8.3: Return read-only snapshot
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit");
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            var snapshot = model.GetAllSegments();

            Assert.IsAssignableFrom<IReadOnlyList<SegmentDisplayItem>>(snapshot);
            Assert.Single(snapshot);
        }

        [Fact]
        public void SegmentDisplayModel_GetAllSegments_SnapshotIsIndependentOfInternalState()
        {
            // Requirement 8.3: Snapshot should not reflect subsequent changes
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test1", "HardCommit");
            var record1 = new SegmentRecord { Commit = commit };
            model.AddSegment(record1);

            var snapshot1 = model.GetAllSegments();
            Assert.Single(snapshot1);

            // Add another segment
            var commit2 = CommitMetadata.From("Test2", "HardCommit");
            var record2 = new SegmentRecord { Commit = commit2 };
            model.AddSegment(record2);

            var snapshot2 = model.GetAllSegments();

            // Original snapshot should still have 1 item (snapshot semantics)
            // But due to shallow copy, this might not hold - documenting expected behavior
            Assert.Single(snapshot1); // Original snapshot unchanged
            Assert.Equal(2, snapshot2.Count); // New snapshot has 2 items
        }

        [Fact]
        public void SegmentDisplayModel_Count_ReturnsCorrectCount()
        {
            var model = new SegmentDisplayModel();
            Assert.Equal(0, model.Count);

            var commit = CommitMetadata.From("Test", "HardCommit");
            model.AddSegment(new SegmentRecord { Commit = commit });
            Assert.Equal(1, model.Count);

            model.AddSegment(new SegmentRecord { Commit = commit });
            Assert.Equal(2, model.Count);
        }

        [Fact]
        public void SegmentDisplayModel_ClearAllSegments_RemovesAllSegments()
        {
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit");
            model.AddSegment(new SegmentRecord { Commit = commit });
            model.AddSegment(new SegmentRecord { Commit = commit });

            model.ClearAllSegments();

            Assert.Equal(0, model.Count);
            Assert.Empty(model.GetAllSegments());
        }

        [Fact]
        public void SegmentDisplayModel_AppliesStaleStateVisualProperties()
        {
            // Requirement 3.4: Stale = 0.3 opacity + gray color
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit");
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            var segmentId = model.GetAllSegments()[0].SegmentId;
            model.UpdateSegmentState(segmentId, SegmentState.Stale);

            var segment = model.GetAllSegments()[0];
            Assert.Equal(0.3, segment.Opacity);
            Assert.NotNull(segment.TextColor);
            Assert.False(segment.IsItalic);
            Assert.False(segment.IsUnderlined);
        }

        [Fact]
        public void SegmentDisplayModel_AppliesRenderedStateVisualProperties()
        {
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit", isDangling: true);
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            var segmentId = model.GetAllSegments()[0].SegmentId;
            model.UpdateSegmentState(segmentId, SegmentState.Rendered);

            var segment = model.GetAllSegments()[0];
            Assert.Equal(1.0, segment.Opacity);
            Assert.False(segment.IsItalic);
            Assert.False(segment.IsUnderlined); // Rendered removes dangling cues
            Assert.Null(segment.TextColor);
        }

        [Fact]
        public async Task SegmentDisplayModel_ThreadSafety_ConcurrentAdds()
        {
            // Requirement 8.1-8.2: Thread-safe operations with lock
            var model = new SegmentDisplayModel();
            var tasks = new Task[10];

            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() =>
                {
                    var commit = CommitMetadata.From($"Test {index}", "HardCommit");
                    var record = new SegmentRecord { Commit = commit };
                    model.AddSegment(record);
                });
            }

            await Task.WhenAll(tasks);

            Assert.Equal(10, model.Count);
        }

        [Fact]
        public async Task SegmentDisplayModel_ThreadSafety_ConcurrentReadsAndWrites()
        {
            // Requirement 8.1-8.4: Thread-safe concurrent operations
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Initial", "HardCommit");
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            var tasks = new Task[20];

            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() =>
                {
                    var newCommit = CommitMetadata.From($"Test {index}", "HardCommit");
                    model.AddSegment(new SegmentRecord { Commit = newCommit });
                });
            }

            for (int i = 10; i < 20; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    var snapshot = model.GetAllSegments();
                    Assert.NotNull(snapshot);
                });
            }

            await Task.WhenAll(tasks);

            Assert.True(model.Count >= 1); // At least the initial segment
        }

        [Fact]
        public void SegmentDisplayModel_UpdateSegmentProperties_UpdatesDanglingFlag()
        {
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit", isDangling: false);
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            // First update to Translated state
            var segmentId = model.GetAllSegments()[0].SegmentId;
            model.UpdateSegmentState(segmentId, SegmentState.Translated);

            // Now update dangling flag
            bool updated = model.UpdateSegmentProperties(segmentId, isDangling: true);

            Assert.True(updated);
            var segment = model.GetAllSegments()[0];
            Assert.True(segment.IsDangling);
            Assert.True(segment.IsUnderlined);
            Assert.NotNull(segment.TextColor);
        }

        [Fact]
        public void SegmentDisplayModel_AddSegment_ThrowsOnNullRecord()
        {
            var model = new SegmentDisplayModel();

            Assert.Throws<ArgumentNullException>(() => model.AddSegment(null!));
        }

        [Fact]
        public void SegmentDisplayModel_AddSegment_ValidatesProperties()
        {
            // Requirement 11.1, 11.3: Property validation
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From(null!, "HardCommit"); // Null text
            var record = new SegmentRecord { Commit = commit };

            model.AddSegment(record);

            var segment = model.GetAllSegments()[0];
            Assert.NotNull(segment.Text);
            Assert.Equal(string.Empty, segment.Text);
        }

        [Fact]
        public void SegmentDisplayModel_MaintainsInsertionOrder()
        {
            // Requirement 4.1: Maintain ordered list
            var model = new SegmentDisplayModel();
            var ids = new Guid[3];

            for (int i = 0; i < 3; i++)
            {
                var commit = CommitMetadata.From($"Text {i}", "HardCommit");
                var record = new SegmentRecord { Commit = commit };
                model.AddSegment(record);
                ids[i] = record.Id;
            }

            var segments = model.GetAllSegments();
            Assert.Equal(3, segments.Count);
            Assert.Equal(ids[0], segments[0].SegmentId);
            Assert.Equal(ids[1], segments[1].SegmentId);
            Assert.Equal(ids[2], segments[2].SegmentId);
        }

        [Fact]
        public void SegmentDisplayModel_UpdatesLastUpdatedTimestamp()
        {
            var model = new SegmentDisplayModel();
            var commit = CommitMetadata.From("Test", "HardCommit");
            var record = new SegmentRecord { Commit = commit };
            model.AddSegment(record);

            var segmentId = model.GetAllSegments()[0].SegmentId;
            var initialTimestamp = model.GetAllSegments()[0].LastUpdated;

            // Wait a bit to ensure timestamp difference
            System.Threading.Thread.Sleep(10);

            model.UpdateSegmentState(segmentId, SegmentState.Translated);

            var updatedTimestamp = model.GetAllSegments()[0].LastUpdated;
            Assert.NotNull(updatedTimestamp);
            Assert.True(updatedTimestamp > initialTimestamp);
        }

        #endregion
    }
}
