using Avalonia.Controls;
using Avalonia.Interactivity;
using m_mslc_overlay.views.components;

namespace m_mslc_overlay
{
    public partial class MainWindow : Window
    {
        private FloatingTextOverlay? _currentOverlay;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentOverlay == null || !_currentOverlay.IsVisible)
            {
                _currentOverlay = new FloatingTextOverlay();
                _currentOverlay.Show();
            }
        }
    }
}