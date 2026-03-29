using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Diagnostics;
using System.IO;
using m_mslc_overlay.views.components;
using m_mslc_overlay.services;

namespace m_mslc_overlay
{
    public partial class MainWindow : Window
    {
        private FloatingTextOverlay? _currentOverlay;
        private AppContainerHiderService _hiderService;
        private LiveCaptionPipeService _pipeService;

        public MainWindow()
        {
            InitializeComponent();
            _hiderService = new AppContainerHiderService();
            _pipeService = new LiveCaptionPipeService();
            
            this.Closing += (s, e) => {
                _hiderService.Dispose();
                _pipeService.Dispose();
            };
        }

        private async void InjectBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_hiderService.HideTargetApp("LiveCaptions"))
                {
                    Debug.WriteLine("LiveCaptions window not found or cannot hide.");
                    return;
                }

                uint pid = _hiderService.TargetProcessId;
                
                string rootDir = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.FullName ?? AppContext.BaseDirectory;
                var loaderPath = Path.Combine(rootDir, "Loader.exe");
                
                if (!File.Exists(loaderPath))
                {
                    loaderPath = Path.Combine(AppContext.BaseDirectory, "Loader.exe");
                }

                var process = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = loaderPath,
                        Arguments = pid.ToString(),
                        UseShellExecute = true,
                        Verb = "runas"
                    }
                };
                
                process.Start();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    HookStatusDot.Fill = SolidColorBrush.Parse("#00FF88");
                    HookStatusText.Text = "Injected";
                    _pipeService.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to inject: {ex.Message}");
            }
        }

        private void OpenOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentOverlay == null || !_currentOverlay.IsVisible)
            {
                _currentOverlay = new FloatingTextOverlay();
                _currentOverlay.Show();
            }
        }

        private DebugWidget? _debugWidget;

        private void OpenDebugWidgetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_debugWidget == null || !_debugWidget.IsVisible)
            {
                _debugWidget = new DebugWidget(_pipeService);
                _debugWidget.Show();
            }
            else
            {
                _debugWidget.Activate();
            }
        }
    }
}