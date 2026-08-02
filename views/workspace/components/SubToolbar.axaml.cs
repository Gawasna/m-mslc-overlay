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
        private Avalonia.Media.IBrush? _recordActiveBrush;
        private Avalonia.Media.IBrush? _defaultForeBrush;

        public SubToolbar()
        {
            InitializeComponent();

            this.Get<Button>("ExportSrtBtn").Click   += (_, _) => (DataContext as WorkspaceViewModel)?.ExportSrt();
            this.Get<Button>("ImportScriptBtn").Click += (_, _) => (DataContext as WorkspaceViewModel)?.ImportScript();

            _recordIcon  = this.Get<MaterialIcon>("RecordIcon");
            _recordLabel = this.Get<TextBlock>("RecordLabel");
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
            // Cập nhật trạng thái ban đầu
            if (DataContext is WorkspaceViewModel vm)
                UpdateRecordVisual(vm.IsRecording);
        }

        private void UpdateRecordVisual(bool isRecording)
        {
            if (_recordLabel == null || _recordIcon == null) return;

            _defaultForeBrush ??= _recordIcon.Foreground;

            if (isRecording)
            {
                _recordLabel.Text = "Stop";
                _recordIcon.Foreground = _recordActiveBrush!;
            }
            else
            {
                _recordLabel.Text = "Record";
                _recordIcon.Foreground = _defaultForeBrush!;
            }
        }
    }
}
