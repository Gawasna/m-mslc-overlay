using System;
using System.Collections.Concurrent;
using Avalonia.Threading;
using m_mslc_overlay.core.Animation;
using m_mslc_overlay.services;
using Xunit;

namespace m_mslc_overlay.core.tests
{
    public class TypewriterCoordinatorTests
    {
        [Fact]
        public void DequeueOldText_RemovesItem()
        {
            var service = new RevisionWindowService();
            var timer = new DispatcherTimer();
            var queue = new ConcurrentQueue<string>();
            queue.Enqueue("hello");
            queue.Enqueue("world");
            
            using var coordinator = new TypewriterCoordinator(service, timer, queue);
            coordinator.DequeueOldText("hello");
            
            Assert.Single(queue);
            Assert.Contains("world", queue);
        }

        [Fact]
        public void EnqueueMergedText_AddsToFront()
        {
            var service = new RevisionWindowService();
            var timer = new DispatcherTimer();
            var queue = new ConcurrentQueue<string>();
            queue.Enqueue("world");
            
            using var coordinator = new TypewriterCoordinator(service, timer, queue);
            coordinator.EnqueueMergedText("hello");
            
            Assert.Equal(2, queue.Count);
            queue.TryDequeue(out var first);
            Assert.Equal("hello", first);
        }
    }
}
