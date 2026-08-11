using Avalonia.Controls;
using Avalonia.Interactivity;
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
            WireReplaceScopeButtons();
            WireSummaryControls();
        }

        private NavPaneViewModel? Vm() => DataContext as NavPaneViewModel;

        // ─── Tab navigation ───────────────────────────────────────────────────

        private void WireTabButtons()
        {
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

        // ─── Glossary + Find ──────────────────────────────────────────────────

        private void WireUtilityButtons()
        {
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

            // Find Next
            var findBtn = this.Find<Button>("FindNextBtn");
            if (findBtn != null)
                findBtn.Click += (_, _) => Vm()?.FindReplace.ExecuteFindNext();

            // Enter key in Find box triggers Find Next
            var findBox = this.Find<TextBox>("FindTextBox");
            if (findBox != null)
            {
                findBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Avalonia.Input.Key.Enter)
                    {
                        Vm()?.FindReplace.ExecuteFindNext();
                        e.Handled = true;
                    }
                };
            }
        }

        // ─── Replace scope RadioButtons + Replace All ─────────────────────────

        private void WireReplaceScopeButtons()
        {
            var bothRadio    = this.Find<RadioButton>("ScopeBothRadio");
            var machineRadio = this.Find<RadioButton>("ScopeMachineRadio");
            var humanRadio   = this.Find<RadioButton>("ScopeHumanRadio");
            var replaceBtn   = this.Find<Button>("ReplaceAllBtn");
            var findBox      = this.Find<TextBox>("FindTextBox");

            if (bothRadio != null)
                bothRadio.IsCheckedChanged += (_, _) =>
                {
                    if (bothRadio.IsChecked == true)
                    {
                        if (Vm() is { } vm) vm.FindReplace.ReplaceScope = ReplaceScope.Both;
                        Vm()?.FindReplace.RefreshWarning();
                    }
                };

            if (machineRadio != null)
                machineRadio.IsCheckedChanged += (_, _) =>
                {
                    if (machineRadio.IsChecked == true)
                    {
                        if (Vm() is { } vm) vm.FindReplace.ReplaceScope = ReplaceScope.MachineOnly;
                        Vm()?.FindReplace.RefreshWarning();
                    }
                };

            if (humanRadio != null)
                humanRadio.IsCheckedChanged += (_, _) =>
                {
                    if (humanRadio.IsChecked == true)
                    {
                        if (Vm() is { } vm) vm.FindReplace.ReplaceScope = ReplaceScope.HumanOnly;
                        Vm()?.FindReplace.RefreshWarning();
                    }
                };

            // Replace All button
            if (replaceBtn != null)
                replaceBtn.Click += (_, _) =>
                {
                    Vm()?.FindReplace.RefreshWarning();  // show warning briefly then execute
                    Vm()?.FindReplace.ExecuteReplaceAll();
                };

            // Refresh warning when Find text changes (already has PropertyChanged but wire directly too)
            if (findBox != null)
                findBox.TextChanged += (_, _) => Vm()?.FindReplace.RefreshWarning();
        }

        // ─── AI Summary controls ──────────────────────────────────────────────

        private void WireSummaryControls()
        {
            var generateBtn   = this.Find<Button>("GenerateSummaryBtn");

            // Resolve NavPane numeric inputs
            var navSegBox  = this.Find<NumericUpDown>("NavTriggerSegmentsInput");
            var navWordBox = this.Find<NumericUpDown>("NavTriggerWordsInput");
            var navTimeBox = this.Find<NumericUpDown>("NavTriggerTimeInput");

            var navRadioSeg  = this.Find<RadioButton>("NavTriggerModeSegments");
            var navRadioWord = this.Find<RadioButton>("NavTriggerModeWords");
            var navRadioTime = this.Find<RadioButton>("NavTriggerModeTime");

            // ── Load current config into Nav inputs ───────────────────────────
            var cfg = services.ConfigManager.Current;
            if (navSegBox  != null) navSegBox.Value  = cfg.SummaryTriggerSegments;
            if (navWordBox != null) navWordBox.Value  = cfg.SummaryTriggerWords;
            if (navTimeBox != null) navTimeBox.Value  = cfg.SummaryTriggerTimeSeconds;

            // Set active radio from config
            switch (cfg.SummaryTriggerMode)
            {
                case services.SummaryTriggerMode.ByWords:
                    if (navRadioWord != null) navRadioWord.IsChecked = true;
                    break;
                case services.SummaryTriggerMode.ByTime:
                    if (navRadioTime != null) navRadioTime.IsChecked = true;
                    break;
                default:
                    if (navRadioSeg != null) navRadioSeg.IsChecked = true;
                    break;
            }

            // ── Persist + refresh when radio selection changes ─────────────────
            void SaveAndRefresh()
            {
                if (navRadioWord?.IsChecked == true)
                    cfg.SummaryTriggerMode = services.SummaryTriggerMode.ByWords;
                else if (navRadioTime?.IsChecked == true)
                    cfg.SummaryTriggerMode = services.SummaryTriggerMode.ByTime;
                else
                    cfg.SummaryTriggerMode = services.SummaryTriggerMode.BySegments;

                services.ConfigManager.Save();

                // Restart time-timer if root VM is available
                if (Tag is viewmodels.transcript.TranscriptViewportViewModel rootVm)
                    rootVm.RefreshSummaryTimer();
            }

            if (navRadioSeg  != null) navRadioSeg.IsCheckedChanged  += (_, _) => { if (navRadioSeg.IsChecked == true)  SaveAndRefresh(); };
            if (navRadioWord != null) navRadioWord.IsCheckedChanged  += (_, _) => { if (navRadioWord.IsChecked == true) SaveAndRefresh(); };
            if (navRadioTime != null) navRadioTime.IsCheckedChanged  += (_, _) => { if (navRadioTime.IsChecked == true) SaveAndRefresh(); };

            // Persist numeric values on change (mode stays active as set by radio)
            if (navSegBox != null)
                navSegBox.ValueChanged += (_, _) =>
                {
                    if (navSegBox.Value.HasValue)
                    {
                        cfg.SummaryTriggerSegments = (int)navSegBox.Value.Value;
                        services.ConfigManager.Save();
                    }
                };

            if (navWordBox != null)
                navWordBox.ValueChanged += (_, _) =>
                {
                    if (navWordBox.Value.HasValue)
                    {
                        cfg.SummaryTriggerWords = (int)navWordBox.Value.Value;
                        services.ConfigManager.Save();
                    }
                };

            if (navTimeBox != null)
                navTimeBox.ValueChanged += (_, _) =>
                {
                    if (navTimeBox.Value.HasValue)
                    {
                        cfg.SummaryTriggerTimeSeconds = (int)navTimeBox.Value.Value;
                        services.ConfigManager.Save();
                        // Re-arm timer with new interval if currently in ByTime mode
                        if (cfg.SummaryTriggerMode == services.SummaryTriggerMode.ByTime
                            && Tag is viewmodels.transcript.TranscriptViewportViewModel rootVm)
                            rootVm.RefreshSummaryTimer();
                    }
                };

            // ── Generate Summary button ───────────────────────────────────────
            if (generateBtn != null)
                generateBtn.Click += (_, _) =>
                {
                    if (Tag is viewmodels.transcript.TranscriptViewportViewModel rootVm)
                        rootVm.RequestSummary();
                };
        }
    }
}
