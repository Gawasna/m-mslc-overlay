using Avalonia.Controls;
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
    }
}
