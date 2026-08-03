using System;
using Avalonia.Controls;
using Material.Icons.Avalonia;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Workspace.Components
{
    public partial class SubToolbar : UserControl
    {
        private MaterialIcon? _recordIcon;
        private TextBlock? _recordLabel;
        private Avalonia.Controls.Shapes.Ellipse? _recordingIndicator;
        private Avalonia.Media.IBrush? _recordActiveBrush;
        private Avalonia.Media.IBrush? _defaultForeBrush;

        public SubToolbar()
        {
            InitializeComponent();

            this.Get<Button>("ExportSrtBtn").Click   += (_, _) => (DataContext as WorkspaceViewModel)?.ExportSrt();
            this.Get<Button>("ImportScriptBtn").Click += (_, _) => (DataContext as WorkspaceViewModel)?.ImportScript();

            _recordIcon  = this.Get<MaterialIcon>("RecordIcon");
            _recordLabel = this.Get<TextBlock>("RecordLabel");
            _recordingIndicator = this.Get<Avalonia.Controls.Shapes.Ellipse>("RecordingIndicator");
            _recordActiveBrush = Avalonia.Media.Brushes.Red;

            this.Get<Button>("RecordBtn").Click += (_, _) =>
            {
                var vm = DataContext as WorkspaceViewModel;
                if (vm == null || !vm.IsOpen) return;
                vm.ToggleRecording();
                UpdateRecordVisual(vm.IsRecording);
            };
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            
            // Unsubscribe from old VM
            if (DataContext is WorkspaceViewModel oldVm)
            {
                oldVm.PropertyChanged -= OnWorkspacePropertyChanged;
            }
            
            // Subscribe to new VM
            if (DataContext is WorkspaceViewModel vm)
            {
                vm.PropertyChanged += OnWorkspacePropertyChanged;
                UpdateRecordVisual(vm.IsRecording);
            }
        }

        private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.IsRecording))
            {
                if (sender is WorkspaceViewModel vm)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateRecordVisual(vm.IsRecording));
                }
            }
        }

        private void UpdateRecordVisual(bool isRecording)
        {
            if (_recordLabel == null || _recordIcon == null || _recordingIndicator == null) return;

            _defaultForeBrush ??= _recordIcon.Foreground;

            if (isRecording)
            {
                _recordLabel.Text = "Stop";
                _recordIcon.Foreground = _recordActiveBrush!;
                _recordingIndicator.IsVisible = true; // Show pulsing red dot
            }
            else
            {
                _recordLabel.Text = "Record";
                _recordIcon.Foreground = _defaultForeBrush!;
                _recordingIndicator.IsVisible = false; // Hide indicator
            }
        }
    }
}
