using Avalonia.Controls;
using m_mslc_overlay.viewmodels.transcript;

namespace m_mslc_overlay.views.content.transcript
{
    public partial class WordStatusBar : UserControl
    {
        public WordStatusBar()
        {
            InitializeComponent();
            WireZoomButtons();
        }

        private void WireZoomButtons()
        {
            this.Get<Button>("ZoomInBtn").Click  += (_, _) => (DataContext as PaperSheetViewModel)?.ZoomIn();
            this.Get<Button>("ZoomOutBtn").Click += (_, _) => (DataContext as PaperSheetViewModel)?.ZoomOut();
        }
    }
}
