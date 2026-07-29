using System;
using System.Drawing;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace m_mslc_overlay.views.controls
{
    /// <summary>
    /// Hosts a CoreWebView2Controller as an HWND overlay on the Avalonia window.
    ///
    /// Key invariants:
    /// 1. HWND is only created on the first ArrangeOverride where the control is
    ///    effectively visible AND has a non-zero arranged size.  This prevents the
    ///    native window from covering Avalonia controls while the host UserControl
    ///    has IsVisible="False" (e.g., workspace idle state).
    /// 2. HWND visibility is synced from ArrangeOverride and IsVisibleProperty changes,
    ///    using IsEffectivelyVisible (CLR property, traverses parent chain) so that
    ///    parent visibility changes are also detected.
    /// 3. HWND bounds are only updated when the arranged size is non-zero.
    /// </summary>
    public class WebView2Control : Control
    {
        private CoreWebView2Controller? _controller;
        private string?                 _pendingHtml;
        private bool                    _initializing;
        private bool                    _initStarted;
        private Window?                 _hostWindow;

        public CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;

        public event EventHandler<CoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived;

        // ─── Attach / detach ──────────────────────────────────────────────────
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is Window window)
                _hostWindow = window;

            // Init is intentionally deferred to ArrangeOverride so that we only
            // create the HWND once the control is effectively visible and sized.
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _hostWindow = null;
            _initStarted = false;
            _controller?.Close();
            _controller = null;
        }

        /// <summary>
        /// Sync HWND visibility when this control's own IsVisible changes.
        /// Parent IsVisible changes are caught in ArrangeOverride via IsEffectivelyVisible.
        /// </summary>
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty || change.Property == BoundsProperty)
                SyncControllerVisibility();
        }

        // ─── Init ─────────────────────────────────────────────────────────────
        private async Task InitAsync(Window window)
        {
            if (_controller != null || _initializing) return;
            _initializing = true;

            try
            {
                var platformHandle = window.TryGetPlatformHandle();
                if (platformHandle == null)
                {
                    System.Diagnostics.Debug.WriteLine("[WebView2Control] platform handle is null – aborting init.");
                    return;
                }

                var env = await CoreWebView2Environment.CreateAsync();
                _controller = await env.CreateCoreWebView2ControllerAsync(platformHandle.Handle);

                // Transparent until content renders — avoids the white flash glitch (or White to avoid transparent flash).
                // Let's use White to avoid Avalonia background bleeding through if that was the "trong suốt" artifact.
                _controller.DefaultBackgroundColor = Color.White;
                // Respect current Avalonia visibility — don't blindly make visible.
                _controller.IsVisible = IsEffectivelyVisible;

                // Enable DevTools for debugging JS failures (F12)
                _controller.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _controller.CoreWebView2.OpenDevToolsWindow();

                _controller.CoreWebView2.WebMessageReceived +=
                    (s, ev) => WebMessageReceived?.Invoke(this, ev);

                SyncControllerVisibility();

                // Navigate to any HTML that was queued before init finished.
                if (_pendingHtml != null)
                {
                    _controller.CoreWebView2.NavigateToString(_pendingHtml);
                    _pendingHtml = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebView2Control] init failed: {ex.Message}");
            }
            finally
            {
                _initializing = false;
            }
        }

        // ─── Public API ───────────────────────────────────────────────────────
        public void NavigateToString(string html)
        {
            if (_controller?.CoreWebView2 != null)
                _controller.CoreWebView2.NavigateToString(html);
            else
                _pendingHtml = html; // queued, applied once controller is ready
        }

        public void PostWebMessage(string json)
        {
            if (_controller?.CoreWebView2 != null)
            {
                // Serialize the json string itself into a valid JS string literal
                string jsStringLiteral = System.Text.Json.JsonSerializer.Serialize(json);
                _controller.CoreWebView2.ExecuteScriptAsync($"if (window.__bridge && window.__bridge.receive) {{ window.__bridge.receive(JSON.parse({jsStringLiteral})); }}");
            }
        }

        // ─── Layout ───────────────────────────────────────────────────────────
        protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
        {
            var size = base.ArrangeOverride(finalSize);

            // Defer init: only create the HWND when the control is effectively visible
            // and has a real size. This is the correct moment because:
            //   • ArrangeOverride fires when PaperSheetView.IsVisible flips to true.
            //   • finalSize is non-zero only when the control actually occupies space.
            if (!_initStarted
                && IsEffectivelyVisible
                && finalSize.Width > 0
                && finalSize.Height > 0
                && _hostWindow != null)
            {
                _initStarted = true;
                if (_hostWindow.IsVisible)
                    Dispatcher.UIThread.Post(() => _ = InitAsync(_hostWindow), DispatcherPriority.Background);
                else
                    _hostWindow.Opened += (s, ev) =>
                        Dispatcher.UIThread.Post(() => _ = InitAsync(_hostWindow), DispatcherPriority.Background);
            }

            // Sync HWND visibility on every arrange pass — catches parent IsVisible changes
            // because Avalonia re-arranges children when a parent becomes visible.
            SyncControllerVisibility();

            return size;
        }

        private void SyncControllerVisibility()
        {
            if (_controller == null) return;

            bool shouldBeVisible = IsEffectivelyVisible
                                   && Bounds.Width  > 0
                                   && Bounds.Height > 0;

            _controller.IsVisible = shouldBeVisible;

            if (shouldBeVisible)
                UpdateBounds();
        }

        private void UpdateBounds()
        {
            if (_controller == null) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            double scaling = topLevel.RenderScaling;
            var pos = this.TranslatePoint(new Avalonia.Point(0, 0), topLevel);
            if (!pos.HasValue) return;

            // Guard: skip if control has collapsed to zero — avoid positioning the HWND
            // at (0,0,0,0) which can leave it visible in some WebView2 versions.
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

            _controller.Bounds = new Rectangle(
                (int)(pos.Value.X * scaling),
                (int)(pos.Value.Y * scaling),
                (int)(Bounds.Width  * scaling),
                (int)(Bounds.Height * scaling));
        }
    }
}
