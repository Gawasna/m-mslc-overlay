using System;
using System.Collections.Generic;
using m_mslc_overlay.core;
using Xunit;

namespace m_mslc_overlay.core.tests
{
    public class VisualStateMapperTests
    {
        [Fact]
        public void HandleCommitted_AddsSegmentToModel()
        {
            var model = new SegmentDisplayModel();
            var tracker = new SegmentTracker();
            using var mapper = new VisualStateMapper(model, tracker);

            var meta = CommitMetadata.From("test", "test");
            tracker.TrackCommit(meta);

            Assert.Equal(1, model.Count);
        }

        [Fact]
        public void HandleTranslated_UpdatesStateAndDangling()
        {
            var model = new SegmentDisplayModel();
            var tracker = new SegmentTracker();
            using var mapper = new VisualStateMapper(model, tracker);

            var meta = CommitMetadata.From("test", "test", isDangling: true);
            var record = tracker.TrackCommit(meta);

            var result = new TranslationResult { Source = meta, Translation = "test translated" };
            tracker.LinkTranslation(result);

            var segments = model.GetAllSegments();
            Assert.Equal(SegmentState.Translated, segments[0].State);
            Assert.True(segments[0].IsDangling);
        }
    }
}
