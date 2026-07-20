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
            this.SizeChanged += PaperSheet_SizeChanged;
        }

        private void PaperSheet_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            double width = e.NewSize.Width;
            var centeringStack = this.FindControl<StackPanel>("PaperCenteringStack");
            var contentStack = this.FindControl<StackPanel>("PaperContentStack");

            if (centeringStack != null && contentStack != null)
            {
                if (width < 728)
                {
                    centeringStack.Margin = new Avalonia.Thickness(56, 0, 56, 16);
                    contentStack.Margin = new Avalonia.Thickness(16, 24);
                }
                else
                {
                    centeringStack.Margin = new Avalonia.Thickness(56, 0, 56, 24);
                    contentStack.Margin = new Avalonia.Thickness(64, 60);
                }
            }
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
