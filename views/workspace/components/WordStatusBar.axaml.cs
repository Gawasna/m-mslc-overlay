using Avalonia.Controls;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Workspace.Components
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
