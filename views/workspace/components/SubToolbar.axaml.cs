using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MMslcOverlay.ViewModels.Workspace;
using m_mslc_overlay.views.dialogs;
using m_mslc_overlay.services;

namespace MMslcOverlay.Views.Workspace.Components
{
    public partial class SubToolbar : UserControl
    {
        /// <summary>
        /// Raised after the user confirms export in ExportDialog.
        /// Payload is the JSON config string from ExportDialog.
        /// Subscribe in the parent window to perform the actual export.
        /// </summary>
        public event Func<string, Task>? ExportRequested;

        public SubToolbar()
        {
            InitializeComponent();

            this.Get<Button>("ExportSrtBtn").Click += async (_, _) =>
            {
                var window = this.VisualRoot as Window;
                if (window == null)
                {
                    // Fallback: delegate to WorkspaceViewModel legacy path
                    (DataContext as WorkspaceViewModel)?.ExportSrt();
                    return;
                }

                // Guard: workspace must be open before opening dialog
                var vm = DataContext as WorkspaceViewModel;
                if (vm?.IsOpen != true)
                {
                    await MessageDialog.ShowAsync(window, "Thông báo",
                        "Vui lòng mở một workspace trước khi xuất file.");
                    return;
                }

                var exportDialog = new ExportDialog(async (jsonPayload) =>
                {
                    LoggerService.Log($"[SubToolbar] Export payload received, forwarding to ExportRequested handler.");

                    // Forward to parent window for actual processing
                    if (ExportRequested != null)
                        await ExportRequested.Invoke(jsonPayload);
                    else
                        LoggerService.Log("[SubToolbar] No ExportRequested handler wired — export not performed.");
                });

                await exportDialog.ShowDialog(window);
            };

            this.Get<Button>("ImportScriptBtn").Click += (_, _) =>
                (DataContext as WorkspaceViewModel)?.ImportScript();
        }
    }
}

