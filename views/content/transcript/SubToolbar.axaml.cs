using Avalonia.Controls;
using m_mslc_overlay.viewmodels.transcript;

namespace m_mslc_overlay.views.content.transcript
{
    public partial class SubToolbar : UserControl
    {
        public SubToolbar()
        {
            InitializeComponent();

            // Wire export/import buttons (toggles are bound directly via ToggleButton.IsChecked)
            this.Get<Button>("ExportSrtBtn").Click   += (_, _) => (DataContext as TranscriptViewportViewModel)?.ExportSrt();
            this.Get<Button>("ImportScriptBtn").Click += (_, _) => (DataContext as TranscriptViewportViewModel)?.ImportScript();
        }
    }
}
