using System;
using Avalonia.Controls;
using Material.Icons.Avalonia;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Workspace.Components
{
    public partial class SubToolbar : UserControl
    {
        public SubToolbar()
        {
            InitializeComponent();

            this.Get<Button>("ExportSrtBtn").Click   += (_, _) => (DataContext as WorkspaceViewModel)?.ExportSrt();
            this.Get<Button>("ImportScriptBtn").Click += (_, _) => (DataContext as WorkspaceViewModel)?.ImportScript();
        }
    }
}
