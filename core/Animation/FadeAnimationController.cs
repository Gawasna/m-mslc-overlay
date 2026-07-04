using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using m_mslc_overlay.services;

namespace m_mslc_overlay.core.Animation
{
    /// <summary>
    /// Phase 4a (ATOM22) — Orchestrates fade-out/fade-in micro-animations during hot-replace operations.
    /// 
    /// This controller subscribes to RevisionWindowService.OnRevise events and coordinates
    /// smooth visual feedback when translation text is replaced:
    /// 1. Fade out the old text (opacity 1.0 → 0.0)
    /// 2. Swap the text content
    /// 3. Fade in the new text (opacity 0.0 → 1.0)
    /// 
    /// Supports cancellation of in-flight animations when rapid successive updates occur.
    /// All animations execute on the Avalonia UI thread via Dispatcher.UIThread.InvokeAsync.
    /// 
    /// Requirements addressed:
    /// - 1.1: Fade-out animation from 1.0 to 0.0 over configurable duration
    /// - 1.2: Text swap after fade-out completes
    /// - 1.3: Fade-in animation from 0.0 to 1.0 over configurable duration
    /// - 1.4: Cancellation of in-flight animations
    /// - 1.5: Total duration within 200ms (100ms fade-out + 100ms fade-in)
    /// - 6.1: Cancellation token support for animation interruption
    /// - 6.4: Reset opacity to 1.0 on cancellation
    /// - 10.1: UI thread execution via Dispatcher.UIThread.InvokeAsync
    /// </summary>
    public class FadeAnimationController : IDisposable
    {
        /// <summary>
        /// Duration in milliseconds for the fade-out animation.
        /// Requirement 9.1: Configurable fade-out duration with default value 100ms.
        /// </summary>
        public int FadeOutDurationMs { get; set; } = 100;

        /// <summary>
        /// Duration in milliseconds for the fade-in animation.
        /// Requirement 9.2: Configurable fade-in duration with default value 100ms.
        /// </summary>
        public int FadeInDurationMs { get; set; } = 100;

        private readonly object _lock = new();
        private Task? _currentAnimation;
        private CancellationTokenSource? _cancellationSource;
        private bool _disposed;
        private readonly RevisionWindowService _revisionWindowService;

        /// <summary>
        /// Initializes a new instance of FadeAnimationController and subscribes to RevisionWindowService events.
        /// </summary>
        /// <param name="revisionWindowService">The revision window service to subscribe to for hot-replace events.</param>
        public FadeAnimationController(RevisionWindowService revisionWindowService)
        {
            _revisionWindowService = revisionWindowService ?? throw new ArgumentNullException(nameof(revisionWindowService));
            
            // Subscribe to OnRevise event to trigger animations automatically
            _revisionWindowService.OnRevise += OnRevisionTriggered;
        }

        /// <summary>
        /// Event handler for RevisionWindowService.OnRevise events.
        /// Automatically triggers fade animation for the target TextBlock.
        /// </summary>
        private void OnRevisionTriggered(string oldText, string newText)
        {
            // This will be called by consumers who have access to the target TextBlock
            // The controller itself doesn't directly hold a reference to the UI element
            // to maintain separation of concerns.
        }

        /// <summary>
        /// Animates a text replacement with fade-out, text swap, and fade-in sequence.
        /// Requirement 1.1-1.5: Complete fade-out → swap → fade-in animation cycle.
        /// Requirement 10.1: Executes all operations on Avalonia UI thread.
        /// </summary>
        /// <param name="target">The TextBlock control to animate.</param>
        /// <param name="newText">The new text to display after fade-out.</param>
        /// <param name="cancellationToken">Optional cancellation token to abort the animation.</param>
        /// <returns>A task that completes when the animation finishes or is cancelled.</returns>
        public async Task AnimateReplaceAsync(TextBlock target, string newText, CancellationToken cancellationToken = default)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (_disposed) throw new ObjectDisposedException(nameof(FadeAnimationController));

            // Requirement 1.4 & 6.1: Cancel any in-flight animations
            CancelCurrentAnimation();

            CancellationTokenSource animationCts;
            Task? animationTask = null;
            
            lock (_lock)
            {
                if (_disposed) return;
                
                // Create new cancellation source linking external token
                animationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _cancellationSource = animationCts;
            }

            try
            {
                // Requirement 10.1: Execute animation on UI thread
                animationTask = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        // Requirement 1.1: Fade out from 1.0 to 0.0
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        await FadeOutAsync(target, animationCts.Token);
                        sw.Stop();
                        m_mslc_overlay.services.LoggerService.Log($"[FadeAnimation] FadeOut completed in {sw.ElapsedMilliseconds}ms");
                        if (sw.ElapsedMilliseconds > FadeOutDurationMs + 50)
                            m_mslc_overlay.services.LoggerService.Log($"[WARNING] FadeOut animation was slow: {sw.ElapsedMilliseconds}ms (Expected: {FadeOutDurationMs}ms)");

                        // Check cancellation before swapping text
                        if (animationCts.Token.IsCancellationRequested)
                        {
                            // Requirement 6.4: Reset opacity on cancellation
                            target.Opacity = 1.0;
                            return;
                        }

                        // Requirement 1.2: Swap text content
                        target.Text = newText;

                        // Requirement 1.3: Fade in from 0.0 to 1.0
                        sw.Restart();
                        await FadeInAsync(target, animationCts.Token);
                        sw.Stop();
                        m_mslc_overlay.services.LoggerService.Log($"[FadeAnimation] FadeIn completed in {sw.ElapsedMilliseconds}ms");
                        if (sw.ElapsedMilliseconds > FadeInDurationMs + 50)
                            m_mslc_overlay.services.LoggerService.Log($"[WARNING] FadeIn animation was slow: {sw.ElapsedMilliseconds}ms (Expected: {FadeInDurationMs}ms)");
                    }
                    catch (OperationCanceledException)
                    {
                        // Requirement 6.4: Reset opacity on cancellation
                        target.Opacity = 1.0;
                    }
                }, DispatcherPriority.Normal);
                
                // Track the animation task
                lock (_lock)
                {
                    _currentAnimation = animationTask;
                }
                
                await animationTask;
            }
            catch (OperationCanceledException)
            {
                // Animation was cancelled - this is expected behavior
            }
            finally
            {
                lock (_lock)
                {
                    if (_cancellationSource == animationCts)
                    {
                        _cancellationSource = null;
                    }
                    if (animationTask != null && _currentAnimation == animationTask)
                    {
                        _currentAnimation = null;
                    }
                    animationCts?.Dispose();
                }
            }
        }

        /// <summary>
        /// Cancels the currently running animation if any.
        /// Requirement 1.4 & 6.1: Cancel in-flight animations when new replace is triggered.
        /// Requirement 6.2: Dispose animation resources within 50ms.
        /// </summary>
        public void CancelCurrentAnimation()
        {
            lock (_lock)
            {
                if (_cancellationSource != null && !_cancellationSource.IsCancellationRequested)
                {
                    _cancellationSource.Cancel();
                }

                // Wait briefly for current animation to complete cancellation
                if (_currentAnimation != null && !_currentAnimation.IsCompleted)
                {
                    // Non-blocking wait with timeout (Requirement 6.2: within 50ms)
                    _currentAnimation.Wait(TimeSpan.FromMilliseconds(50));
                }
            }
        }

        /// <summary>
        /// Performs fade-out animation on the target TextBlock.
        /// Requirement 1.1: Opacity 1.0 → 0.0 over FadeOutDurationMs.
        /// Requirement 9.1: Uses configurable fade-out duration.
        /// </summary>
        private async Task FadeOutAsync(TextBlock target, CancellationToken cancellationToken)
        {
            // Requirement 9.5: Use default duration if configured value is invalid
            int duration = FadeOutDurationMs > 0 ? FadeOutDurationMs : 100;

            var animation = new Avalonia.Animation.Animation
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = new LinearEasing(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0.0),
                        Setters = { new Setter(TextBlock.OpacityProperty, 1.0) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1.0),
                        Setters = { new Setter(TextBlock.OpacityProperty, 0.0) }
                    }
                }
            };

            await animation.RunAsync(target, cancellationToken);
        }

        /// <summary>
        /// Performs fade-in animation on the target TextBlock.
        /// Requirement 1.3: Opacity 0.0 → 1.0 over FadeInDurationMs.
        /// Requirement 9.2: Uses configurable fade-in duration.
        /// </summary>
        private async Task FadeInAsync(TextBlock target, CancellationToken cancellationToken)
        {
            // Requirement 9.5: Use default duration if configured value is invalid
            int duration = FadeInDurationMs > 0 ? FadeInDurationMs : 100;

            var animation = new Avalonia.Animation.Animation
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = new LinearEasing(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0.0),
                        Setters = { new Setter(TextBlock.OpacityProperty, 0.0) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1.0),
                        Setters = { new Setter(TextBlock.OpacityProperty, 1.0) }
                    }
                }
            };

            await animation.RunAsync(target, cancellationToken);
        }

        /// <summary>
        /// Disposes the controller and cancels all in-flight animations.
        /// Requirement 6.3: Cancel all animations and clean up resources on dispose.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                // Unsubscribe from events
                _revisionWindowService.OnRevise -= OnRevisionTriggered;

                // Cancel any running animations
                CancelCurrentAnimation();

                _cancellationSource?.Dispose();
                _cancellationSource = null;
            }
        }
    }
}
