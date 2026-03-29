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
        private InjectorService _injectorService;
        private AIService _aiService;

        public MainWindow()
        {
            InitializeComponent();
            _hiderService = new AppContainerHiderService();
            _pipeService = new LiveCaptionPipeService();
            _injectorService = new InjectorService();
            _aiService = new AIService();
            
            _aiService.ContextTopic = TopicInput.Text ?? "Game/Phim";
            TopicInput.TextChanged += (s, e) => {
                _aiService.ContextTopic = TopicInput.Text ?? "Game/Phim";
            };

            _pipeService.OnFinalSentenceReceived += async (txt) => {
                await _aiService.TranslateSentenceAsync(txt);
            };

            _aiService.OnTranslationCompleted += (translatedTxt) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    if (_currentOverlay != null && _currentOverlay.IsVisible)
                    {
                        _currentOverlay.EnqueueText(translatedTxt);
                    }
                });
            };

            this.Closing += (s, e) => {
                _hiderService.Dispose();
                _pipeService.Dispose();
            };
        }

        private async void InjectBtn_Click(object sender, RoutedEventArgs e)
        {
            uint pid = _hiderService.PreFindTargetProcessId("LiveCaptions");
            if (pid == 0)
            {
                Debug.WriteLine("Trình LiveCaptions chưa bật! Vui lòng bật Live Captions của Windows trước khi Inject.");
                return;
            }

            bool success = await _injectorService.InjectAsync(pid);

            if (success)
            {
                HookStatusDot.Fill = SolidColorBrush.Parse("#00FF88");
                HookStatusText.Text = "Injected";
                _pipeService.Start();
            }
            else
            {
                HookStatusDot.Fill = SolidColorBrush.Parse("#FF3333");
                HookStatusText.Text = "Failed";
                Debug.WriteLine("Quá trình Inject thất bại hoặc bị từ chối quyền Administrator.");
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
                _debugWidget.OnInterruptRequested += () => {
                    _currentOverlay?.ClearQueueAndText();
                };
                _debugWidget.Show();
            }
            else
            {
                _debugWidget.Activate();
            }
        }
    }
}