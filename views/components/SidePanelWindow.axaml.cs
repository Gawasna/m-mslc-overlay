using Avalonia.Controls;
using Avalonia.Input;
using System;

namespace m_mslc_overlay.views.components
{
    public partial class SidePanelWindow : Window
    {
        public Action? OnClosedAction { get; set; }

        public SidePanelWindow()
        {
            InitializeComponent();
            this.Closed += (s, e) => OnClosedAction?.Invoke();
        }

        private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                this.BeginMoveDrag(e);
            }
        }
    }
}
