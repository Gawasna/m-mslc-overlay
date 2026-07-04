using System;
using System.Collections.Generic;
using Avalonia.Controls.Documents;
using m_mslc_overlay.core;
using Xunit;

namespace m_mslc_overlay.core.tests
{
    public class DiffMatchPatchRendererTests
    {
        [Fact]
        public void ComputeDelta_ReturnsInsertWhenDifferent()
        {
            var renderer = new DiffMatchPatchRenderer();
            var deltas = renderer.ComputeDelta("hello");
            
            Assert.Single(deltas);
            Assert.Equal(DiffOperation.Insert, deltas[0].Operation);
            Assert.Equal("hello", deltas[0].NewText);
        }

        [Fact]
        public void ComputeDelta_ReturnsEmptyWhenSame()
        {
            var renderer = new DiffMatchPatchRenderer();
            renderer.ComputeDelta("hello");
            var deltas = renderer.ComputeDelta("hello");
            
            Assert.Empty(deltas);
        }
    }
}
