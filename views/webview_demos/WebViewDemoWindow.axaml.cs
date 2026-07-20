using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace m_mslc_overlay.views.webview_demos
{
    public partial class WebViewDemoWindow : Window
    {
        private CoreWebView2Controller? _controller;
        private CoreWebView2? _core;

        private DispatcherTimer? _sttTimer;
        private int _sentenceIndex = 0;
        private string _htmlAssetPath = string.Empty;
        private bool _webViewReady = false;

        private string[] _mockData = new[] {
            "00:00:15|SPK_1|This is an AI generated transcript.",
            "00:00:18|SPK_1|It uses the magic cursor to insert text.",
            "00:00:22|SPK_2|You can edit anywhere in this document.",
            "00:00:25|SPK_2|The UI is rendered using web engines."
        };

        public WebViewDemoWindow()
        {
            InitializeComponent();
        }

        public WebViewDemoWindow(string title, string htmlAssetPath)
        {
            InitializeComponent();
            Title = title;
            _htmlAssetPath = htmlAssetPath;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (!string.IsNullOrEmpty(_htmlAssetPath))
            {
                // Must run after window is fully opened so we have a real HWND
                Dispatcher.UIThread.Post(() => _ = InitWebView2Async(), DispatcherPriority.Loaded);
            }
        }

        private async Task InitWebView2Async()
        {
            try
            {
                services.ThemeManager.Instance.PropertyChanged += OnThemeChanged;
                var rootGrid = this.FindControl<Grid>("RootGrid");
                if (rootGrid == null) return;

                // Get parent HWND from the Avalonia window
                var platformHandle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle();
                if (platformHandle == null)
                {
                    Console.WriteLine("Could not get platform handle.");
                    return;
                }
                var parentHwnd = platformHandle.Handle;

                // Create environment and controller
                var env = await CoreWebView2Environment.CreateAsync();

                // CoreWebView2Controller lives in the COM world; we give it the parent HWND
                _controller = await env.CreateCoreWebView2ControllerAsync(parentHwnd);
                _core = _controller.CoreWebView2;

                // Match WebView2 bounds to the RootGrid
                UpdateWebViewBounds(rootGrid);

                // Wire resize so WebView2 tracks the panel
                rootGrid.SizeChanged += (_, _) => UpdateWebViewBounds(rootGrid);

                // Make background transparent so Avalonia BgCardBrush shows through
                _controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;

                _core.NavigationCompleted += (s, e) => ApplyThemeToWebView();

                _webViewReady = true;
                LoadAssetHtml(_htmlAssetPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to init WebView2: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
        }

        private void UpdateWebViewBounds(Control panel)
        {
            if (_controller == null) return;
            var bounds = panel.Bounds;
            // Convert Avalonia logical pixels to device pixels
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            var pos = panel.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
            _controller.Bounds = new System.Drawing.Rectangle(
                (int)(pos.X * scaling),
                (int)(pos.Y * scaling),
                (int)(bounds.Width * scaling),
                (int)(bounds.Height * scaling)
            );
            _controller.IsVisible = true;
        }

        private void LoadAssetHtml(string assetPath)
        {
            if (_core == null) return;
            try
            {
                var uri = new Uri($"avares://m-mslc-overlay/{assetPath}");
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                string html = reader.ReadToEnd();
                _core.NavigateToString(html);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error loading html: " + ex.Message);
            }
        }

        private void SimulateSttBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_sttTimer == null)
            {
                _sttTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _sttTimer.Tick += OnSttTick;
                _sttTimer.Start();
                var statusText = this.FindControl<TextBlock>("StatusText");
                if (statusText != null) statusText.Text = "Magic Cursor: Active";
            }
            else
            {
                _sttTimer.Stop();
                _sttTimer = null;
                var statusText = this.FindControl<TextBlock>("StatusText");
                if (statusText != null) statusText.Text = "Magic Cursor: Stopped";
            }
        }

        private void OnSttTick(object? sender, EventArgs e)
        {
            if (!_webViewReady || _core == null) return;

            if (_sentenceIndex >= _mockData.Length) _sentenceIndex = 0;

            string rawData = _mockData[_sentenceIndex];
            _sentenceIndex++;

            var parts = rawData.Split('|');
            string ts = parts[0];
            string spk = parts[1];
            string text = parts[2].Replace("'", "\\'");

            string js = $"if (window.simulateMagicCursor) window.simulateMagicCursor('{ts}', '{spk}', '{text}');";
            _ = _core.ExecuteScriptAsync(js);
        }

        private void OnThemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(services.ThemeManager.IsDark))
            {
                ApplyThemeToWebView();
            }
        }

        private void ApplyThemeToWebView()
        {
            if (_core == null || !_webViewReady) return;
            bool isDark = services.ThemeManager.Instance.IsDark;
            
            try {
                _core.Profile.PreferredColorScheme = isDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            } catch { }

            string textColor = isDark ? "#F2F2F7" : "#111111";
            string js = $@"
                if (!document.getElementById('dynamic-theme-style')) {{
                    var style = document.createElement('style');
                    style.id = 'dynamic-theme-style';
                    document.head.appendChild(style);
                }}
                document.getElementById('dynamic-theme-style').innerHTML = 'body {{ color: {textColor} !important; }}';
                
                if (typeof monaco !== 'undefined' && monaco.editor) {{
                    monaco.editor.setTheme('{ (isDark ? "vs-dark" : "vs") }');
                }}
            ";
            _ = _core.ExecuteScriptAsync(js);
        }

        protected override void OnClosed(EventArgs e)
        {
            services.ThemeManager.Instance.PropertyChanged -= OnThemeChanged;
            _sttTimer?.Stop();
            _controller?.Close();
            base.OnClosed(e);
        }
    }
}
