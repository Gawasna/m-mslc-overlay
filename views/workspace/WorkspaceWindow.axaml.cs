using Avalonia.Controls;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Workspace;

public partial class WorkspaceWindow : Window
{
    public WorkspaceWindow()
    {
        InitializeComponent();
        DataContext = new WorkspaceViewModel();
    }
}
