using Avalonia.Controls;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Workspace.Components
{
    public partial class DocumentNavigationPane : UserControl
    {
        public DocumentNavigationPane()
        {
            InitializeComponent();
            WireTabButtons();
            WireUtilityButtons();
        }

        private void WireTabButtons()
        {
            NavPaneViewModel? Vm() => DataContext as NavPaneViewModel;

            this.Get<Button>("BtnSpeaker").Click     += (_, _) => Vm()?.SwitchState(NavPaneState.SpeakerAnnotation);
            this.Get<Button>("BtnFindReplace").Click += (_, _) => Vm()?.SwitchState(NavPaneState.FindReplace);
            this.Get<Button>("BtnSummary").Click     += (_, _) => Vm()?.SwitchState(NavPaneState.AiSummary);
            this.Get<Button>("BtnAutoCorrect").Click += (_, _) => Vm()?.SwitchState(NavPaneState.AiAutoCorrect);
            this.Get<Button>("BtnGlossary").Click    += (_, _) => Vm()?.SwitchState(NavPaneState.Glossary);
            this.Get<Button>("BtnClose").Click       += (_, _) => Vm()?.Close();
            this.Get<Button>("BtnToggleCompact").Click += (_, _) => Vm()?.ToggleCompact();

            this.Get<Button>("BtnSpeakerCompact").Click     += (_, _) => Vm()?.SwitchState(NavPaneState.SpeakerAnnotation);
            this.Get<Button>("BtnFindReplaceCompact").Click += (_, _) => Vm()?.SwitchState(NavPaneState.FindReplace);
            this.Get<Button>("BtnSummaryCompact").Click     += (_, _) => Vm()?.SwitchState(NavPaneState.AiSummary);
            this.Get<Button>("BtnAutoCorrectCompact").Click += (_, _) => Vm()?.SwitchState(NavPaneState.AiAutoCorrect);
            this.Get<Button>("BtnGlossaryCompact").Click    += (_, _) => Vm()?.SwitchState(NavPaneState.Glossary);
            this.Get<Button>("BtnToggleCompactExpand").Click += (_, _) => Vm()?.ToggleCompact();
        }

        private void WireUtilityButtons()
        {
            NavPaneViewModel? Vm() => DataContext as NavPaneViewModel;

            // Glossary: add entry on button click
            this.Get<Button>("AddGlossaryEntryBtn").Click += (_, _) =>
            {
                var term = this.Find<TextBox>("GlossaryTermBox")?.Text ?? string.Empty;
                var def  = this.Find<TextBox>("GlossaryDefBox")?.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(term))
                {
                    Vm()?.AddGlossaryEntry(term, def);
                    var termBox = this.Find<TextBox>("GlossaryTermBox");
                    var defBox  = this.Find<TextBox>("GlossaryDefBox");
                    if (termBox != null) termBox.Text = string.Empty;
                    if (defBox  != null) defBox.Text  = string.Empty;
                }
            };
        }

        // P3.5: Open preferences dialog from fallback panel
        private void OpenPreferencesBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var mainWindow = Avalonia.VisualTree.VisualExtensions.GetVisualRoot(this) as m_mslc_overlay.MainWindow;
            if (mainWindow != null)
            {
                var prefs = new m_mslc_overlay.views.dialogs.PreferencesDialog();
                prefs.ShowDialog(mainWindow);
            }
        }
    }
}
