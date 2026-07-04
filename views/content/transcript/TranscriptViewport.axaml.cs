using Avalonia.Controls;
using m_mslc_overlay.viewmodels.transcript;

namespace m_mslc_overlay.views.content.transcript
{
    public partial class TranscriptViewport : UserControl
    {
        public TranscriptViewport()
        {
            InitializeComponent();
            DataContext = new TranscriptViewportViewModel();
        }

        /// <summary>
        /// Exposes the root ViewModel for MainWindow to push data into.
        /// Typed accessor so MainWindow does not need to cast DataContext.
        /// </summary>
        public TranscriptViewportViewModel ViewModel =>
            (TranscriptViewportViewModel)DataContext!;
    }
}
