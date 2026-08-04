using System;
using Avalonia.Controls;
using Material.Icons.Avalonia;
using MMslcOverlay.ViewModels.Workspace;
using m_mslc_overlay.views.dialogs;
using m_mslc_overlay.services;

namespace MMslcOverlay.Views.Workspace.Components
{
    public partial class SubToolbar : UserControl
    {
        public SubToolbar()
        {
            InitializeComponent();

            this.Get<Button>("ExportSrtBtn").Click   += async (_, _) => {
                var window = this.VisualRoot as Window;
                if (window != null)
                {
                    var exportDialog = new ExportDialog((jsonPayload) => {
                        LoggerService.Log($"[SubToolbar] Export callback triggered with JSON payload:\n{jsonPayload}");
                    });
                    await exportDialog.ShowDialog(window);
                }
                else
                {
                    (DataContext as WorkspaceViewModel)?.ExportSrt();
                }
            };
            this.Get<Button>("ImportScriptBtn").Click += (_, _) => (DataContext as WorkspaceViewModel)?.ImportScript();
        }
    }
}
