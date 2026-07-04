using Avalonia.Controls;
using m_mslc_overlay.viewmodels.transcript;

namespace m_mslc_overlay.views.content.transcript
{
    public partial class RecordingHeaderBar : UserControl
    {
        public RecordingHeaderBar()
        {
            InitializeComponent();

            // Wire up stop button
            this.Get<Button>("StopButton").Click += (_, _) =>
            {
                if (DataContext is RecordingSessionViewModel vm)
                    vm.StopRecording();
            };
        }
    }
}
