using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace m_mslc_overlay.core.Animation
{
    public class SlideAnimationController : IDisposable
    {
        public int SlideUpDurationMs { get; set; } = 300;
        public Easing EasingFunction { get; set; } = new CubicEaseOut();

        private readonly object _lock = new();
        private Task? _currentAnimation;
        private CancellationTokenSource? _cancellationSource;
        private bool _disposed;

        public async Task AnimateSlideUpAsync(Control target, double lineHeight, Action removeOldestAction, CancellationToken cancellationToken = default)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (_disposed) throw new ObjectDisposedException(nameof(SlideAnimationController));

            CancelCurrentAnimation();

            CancellationTokenSource animationCts;
            Task? animationTask = null;

            lock (_lock)
            {
                if (_disposed) return;
                animationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _cancellationSource = animationCts;
            }

            try
            {
                animationTask = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        var transform = new TranslateTransform();
                        target.RenderTransform = transform;

                        int duration = SlideUpDurationMs > 0 ? SlideUpDurationMs : 300;
                        int midpoint = duration / 2;

                        var animation = new Avalonia.Animation.Animation
                        {
                            Duration = TimeSpan.FromMilliseconds(duration),
                            Easing = EasingFunction,
                            Children =
                            {
                                new KeyFrame
                                {
                                    Cue = new Cue(0.0),
                                    Setters = { new Setter(TranslateTransform.YProperty, 0.0) }
                                },
                                new KeyFrame
                                {
                                    Cue = new Cue(1.0),
                                    Setters = { new Setter(TranslateTransform.YProperty, -lineHeight) }
                                }
                            }
                        };

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var animTask = animation.RunAsync(target, animationCts.Token);
                        
                        // Schedule midpoint removal
                        _ = Task.Run(async () => {
                            await Task.Delay(midpoint, animationCts.Token);
                            if (!animationCts.Token.IsCancellationRequested)
                            {
                                Dispatcher.UIThread.Post(() => removeOldestAction?.Invoke());
                            }
                        });

                        await animTask;
                        sw.Stop();
                        m_mslc_overlay.services.LoggerService.Log($"[SlideAnimation] SlideUp completed in {sw.ElapsedMilliseconds}ms");
                        if (sw.ElapsedMilliseconds > duration + 50)
                            m_mslc_overlay.services.LoggerService.Log($"[WARNING] SlideUp animation was slow: {sw.ElapsedMilliseconds}ms (Expected: {duration}ms)");

                        if (!animationCts.Token.IsCancellationRequested)
                        {
                            transform.Y = 0.0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (target.RenderTransform is TranslateTransform t)
                        {
                            t.Y = 0.0;
                        }
                    }
                }, DispatcherPriority.Normal);

                lock (_lock)
                {
                    _currentAnimation = animationTask;
                }

                await animationTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_lock)
                {
                    if (_cancellationSource == animationCts) _cancellationSource = null;
                    if (_currentAnimation == animationTask) _currentAnimation = null;
                    animationCts?.Dispose();
                }
            }
        }

        public void CancelCurrentAnimation()
        {
            lock (_lock)
            {
                if (_cancellationSource != null && !_cancellationSource.IsCancellationRequested)
                {
                    _cancellationSource.Cancel();
                }
                if (_currentAnimation != null && !_currentAnimation.IsCompleted)
                {
                    _currentAnimation.Wait(TimeSpan.FromMilliseconds(50));
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                CancelCurrentAnimation();
                _cancellationSource?.Dispose();
                _cancellationSource = null;
            }
        }
    }
}
