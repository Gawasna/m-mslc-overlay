using Avalonia.Controls;
using m_mslc_overlay.viewmodels.transcript;

namespace m_mslc_overlay.views.content.transcript
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
    }
}
