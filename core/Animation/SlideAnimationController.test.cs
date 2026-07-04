using System;
using System.Threading;
using Avalonia.Controls;
using m_mslc_overlay.core.Animation;
using Xunit;

namespace m_mslc_overlay.core.tests
{
    public class SlideAnimationControllerTests
    {
        [Fact]
        public void SlideAnimationController_Init_SetsDefaults()
        {
            using var controller = new SlideAnimationController();
            Assert.Equal(300, controller.SlideUpDurationMs);
            Assert.NotNull(controller.EasingFunction);
        }

        [Fact]
        public void SlideAnimationController_CancelCurrentAnimation_CompletesGracefully()
        {
            using var controller = new SlideAnimationController();
            controller.CancelCurrentAnimation();
            Assert.True(true);
        }

        [Fact]
        public void SlideAnimationController_Dispose_CleansUp()
        {
            var controller = new SlideAnimationController();
            controller.Dispose();
            controller.Dispose();
            Assert.True(true);
        }
    }
}
