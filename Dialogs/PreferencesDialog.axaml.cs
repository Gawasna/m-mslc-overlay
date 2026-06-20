using Avalonia.Controls;
using Avalonia.Interactivity;

namespace m_mslc_overlay.Dialogs
{
    public partial class PreferencesDialog : Window
    {
        public PreferencesDialog()
        {
            InitializeComponent();
        }

        private void TabSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (TabSelector == null) return;
            
            // Hide all tabs
            if (TabGeneral != null) TabGeneral.IsVisible = false;
            if (TabAI != null) TabAI.IsVisible = false;
            if (TabAppearance != null) TabAppearance.IsVisible = false;
            if (TabAdvanced != null) TabAdvanced.IsVisible = false;

            // Show selected tab
            switch (TabSelector.SelectedIndex)
            {
                case 0:
                    if (TabGeneral != null) TabGeneral.IsVisible = true;
                    break;
                case 1:
                    if (TabAI != null) TabAI.IsVisible = true;
                    break;
                case 2:
                    if (TabAppearance != null) TabAppearance.IsVisible = true;
                    break;
                case 3:
                    if (TabAdvanced != null) TabAdvanced.IsVisible = true;
                    break;
            }
        }

        private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
