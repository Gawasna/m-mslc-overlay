using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using System.Collections.Specialized;
using m_mslc_overlay.viewmodels.transcript;

namespace m_mslc_overlay.views.content.transcript
{
    public partial class PaperSheet : UserControl
    {
        private ScrollViewer? _scrollViewer;

        public PaperSheet()
        {
            InitializeComponent();
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            _scrollViewer = this.Find<ScrollViewer>("MainScrollViewer");
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);

            // Lazy-init scroll viewer ref if template already applied
            _scrollViewer ??= this.Find<ScrollViewer>("MainScrollViewer");

            if (DataContext is PaperSheetViewModel vm)
            {
                // Subscribe to collection changes to auto-scroll
                vm.Segments.CollectionChanged += OnSegmentsChanged;
            }
        }

        private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (DataContext is not PaperSheetViewModel vm) return;
            if (!vm.AutoScroll) return;
            if (e.Action != NotifyCollectionChangedAction.Add) return;

            // Defer to after layout pass so ScrollToEnd captures new height
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _scrollViewer?.ScrollToEnd();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
