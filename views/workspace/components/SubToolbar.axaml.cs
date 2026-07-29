using Avalonia.Controls;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Workspace.Components
{
    public partial class SubToolbar : UserControl
    {
        public SubToolbar()
        {
            InitializeComponent();

            // Wire export/import buttons (toggles are bound directly via ToggleButton.IsChecked)
            this.Get<Button>("ExportSrtBtn").Click   += (_, _) => (DataContext as WorkspaceViewModel)?.ExportSrt();
            this.Get<Button>("ImportScriptBtn").Click += (_, _) => (DataContext as WorkspaceViewModel)?.ImportScript();
        }
    }
}
