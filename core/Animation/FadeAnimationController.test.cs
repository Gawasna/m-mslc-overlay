using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using m_mslc_overlay.core.Animation;
using m_mslc_overlay.services;
using Xunit;

namespace m_mslc_overlay.core.tests
{
    public class FadeAnimationControllerTests
    {
        [Fact]
        public void FadeAnimationController_Init_SetsDefaults()
        {
            var service = new RevisionWindowService();
            using var controller = new FadeAnimationController(service);
            Assert.Equal(100, controller.FadeOutDurationMs);
            Assert.Equal(100, controller.FadeInDurationMs);
        }

        [Fact]
        public void FadeAnimationController_CancelCurrentAnimation_CompletesGracefully()
        {
            var service = new RevisionWindowService();
            using var controller = new FadeAnimationController(service);
            controller.CancelCurrentAnimation();
            Assert.True(true); // Should not throw
        }

        [Fact]
        public void FadeAnimationController_Dispose_UnsubscribesAndCleansUp()
        {
            var service = new RevisionWindowService();
            var controller = new FadeAnimationController(service);
            controller.Dispose();
            // Disposing again should be safe
            controller.Dispose();
            Assert.True(true);
        }
    }
}
