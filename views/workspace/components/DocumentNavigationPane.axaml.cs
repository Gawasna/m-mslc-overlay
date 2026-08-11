using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Workspace.Components
{
    public partial class DocumentNavigationPane : UserControl
    {
        // Tracks which speaker uid is currently pending a merge target selection
        private string? _pendingMergeSourceUid;
        // Tracks which speaker uid owns the segment being reassigned
        private string? _pendingReassignSourceUid;
        // Tracks the segment slice being reassigned
        private SpeakerSegmentSlice? _pendingReassignSlice;

        public DocumentNavigationPane()
        {
            InitializeComponent();
            WireTabButtons();
            WireUtilityButtons();
            WireSpeakerPanel();
        }

        // ─── Tab navigation ──────────────────────────────────────────────────

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

        // ─── Glossary + Find/Replace utility buttons ─────────────────────────

        private void WireUtilityButtons()
        {
            NavPaneViewModel? Vm() => DataContext as NavPaneViewModel;

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

            var findBtn = this.Find<Button>("FindNextBtn");
            if (findBtn != null)
                findBtn.Click += (_, _) => Vm()?.FindReplace.ExecuteFindNext();

            var replaceAllBtn = this.Find<Button>("ReplaceAllBtn");
            if (replaceAllBtn != null)
            {
                replaceAllBtn.Click += (_, _) => 
                {
                    var fr = Vm()?.FindReplace;
                    if (fr != null && fr.ReplaceAllAction != null)
                    {
                        fr.ReplaceAllAction(fr.FindText, fr.ReplaceText, fr.ReplaceScope);
                    }
                };
            }

            var generateSummaryBtn = this.Find<Button>("GenerateSummaryBtn");
            if (generateSummaryBtn != null)
            {
                generateSummaryBtn.Click += async (_, _) => 
                {
                    var action = Vm()?.GenerateSummaryAction;
                    if (action != null) await action();
                };
            }

            var findBox = this.Find<TextBox>("FindTextBox");
            if (findBox != null)
            {
                findBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        Vm()?.FindReplace.ExecuteFindNext();
                        e.Handled = true;
                    }
                };
            }
        }

        // ─── Speaker annotation panel wiring ─────────────────────────────────

        private void WireSpeakerPanel()
        {
            NavPaneViewModel? Vm() => DataContext as NavPaneViewModel;

            // Refresh suggestions button
            this.Get<Button>("RefreshSuggestionsBtn").Click += async (_, _) =>
            {
                if (Vm()?.RefreshMergeSuggestionsRequested != null)
                    await Vm()!.RefreshMergeSuggestionsRequested();
            };

            // Cancel merge picker
            this.Get<Button>("CancelMergeBtn").Click += (_, _) => CloseMergePicker();

            // --- Speaker list item events (event delegation via AddHandler on ItemsControl) ---
            // Avalonia DataTemplates don't support direct code-behind wiring, so we bubble
            // events up through the ItemsControl using the routed event system.

            var speakerList = this.Get<ItemsControl>("SpeakerList");

            // DoubleTapped on NameLabel → activate inline editor
            speakerList.AddHandler(DoubleTappedEvent, OnSpeakerLabelDoubleTapped, RoutingStrategies.Bubble);

            // KeyDown on NameEditor → commit on Enter, cancel on Escape
            speakerList.AddHandler(KeyDownEvent, OnSpeakerEditorKeyDown, RoutingStrategies.Bubble);

            // LostFocus on NameEditor → commit
            speakerList.AddHandler(LostFocusEvent, OnSpeakerEditorLostFocus, RoutingStrategies.Bubble);

            // Click on MergeSpeakerBtn → open merge picker
            speakerList.AddHandler(Button.ClickEvent, OnSpeakerItemButtonClick, RoutingStrategies.Bubble);

            // Merge target picker — target buttons inside MergeTargetList
            var mergeTargetList = this.Get<ItemsControl>("MergeTargetList");
            mergeTargetList.AddHandler(Button.ClickEvent, OnMergeTargetClick, RoutingStrategies.Bubble);

            // Suggestion list — accept / dismiss
            var suggestionList = this.Get<ItemsControl>("SuggestionList");
            suggestionList.AddHandler(Button.ClickEvent, OnSuggestionButtonClick, RoutingStrategies.Bubble);
        }

        // ─── Inline edit handlers ─────────────────────────────────────────────

        private void OnSpeakerLabelDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is TextBlock label && label.Name == "NameLabel")
            {
                var panel = label.Parent as Panel;
                var editor = panel?.Find<TextBox>("NameEditor");
                if (editor == null) return;

                label.IsVisible = false;
                editor.IsVisible = true;
                editor.Text = label.Text;
                editor.Focus();
                editor.SelectAll();
                e.Handled = true;
            }
        }

        private void OnSpeakerEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is TextBox editor && editor.Name == "NameEditor")
            {
                if (e.Key == Key.Enter)
                {
                    CommitSpeakerEdit(editor);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    CancelSpeakerEdit(editor);
                    e.Handled = true;
                }
            }
        }

        private void OnSpeakerEditorLostFocus(object? sender, RoutedEventArgs e)
        {
            if (e.Source is TextBox editor && editor.Name == "NameEditor")
                CommitSpeakerEdit(editor);
        }

        private async void CommitSpeakerEdit(TextBox editor)
        {
            var vm = DataContext as NavPaneViewModel;
            if (vm == null) return;

            // Resolve the SpeakerAnnotation from DataContext climbing
            var speakerAnnotation = FindAncestorDataContext<SpeakerAnnotation>(editor);
            if (speakerAnnotation == null)
            {
                RestoreLabel(editor);
                return;
            }

            string newName = (editor.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName)) newName = speakerAnnotation.DisplayName;

            speakerAnnotation.DisplayName = newName;
            RestoreLabel(editor);

            if (vm.SpeakerRenameRequested != null)
                await vm.SpeakerRenameRequested(speakerAnnotation.SpeakerKey, newName);
        }

        private void CancelSpeakerEdit(TextBox editor)
        {
            RestoreLabel(editor);
        }

        private static void RestoreLabel(TextBox editor)
        {
            var panel = editor.Parent as Panel;
            var label = panel?.Find<TextBlock>("NameLabel");
            if (label != null) label.IsVisible = true;
            editor.IsVisible = false;
        }

        // ─── Merge speaker handlers ───────────────────────────────────────────

        private void OnSpeakerItemButtonClick(object? sender, RoutedEventArgs e)
        {
            if (e.Source is Button btn)
            {
                if (btn.Name == "MergeSpeakerBtn" && btn.Tag is string sourceUid)
                {
                    OpenMergePicker(sourceUid);
                    e.Handled = true;
                }
                else if (btn.Name == "ReassignSegBtn" && btn.Tag is SpeakerSegmentSlice slice)
                {
                    // Find the parent speaker annotation
                    var annotation = FindAncestorDataContext<SpeakerAnnotation>(btn);
                    if (annotation != null)
                        OpenReassignPicker(annotation.SpeakerKey, slice);
                    e.Handled = true;
                }
            }
        }

        private void OpenMergePicker(string sourceUid)
        {
            _pendingMergeSourceUid = sourceUid;
            _pendingReassignSlice = null;

            var panel = this.Find<Border>("MergePickerPanel");
            var title = this.Find<TextBlock>("MergePickerTitle");
            if (panel == null) return;

            // Find display name of source
            var vm = DataContext as NavPaneViewModel;
            string sourceName = sourceUid;
            if (vm != null)
                foreach (var s in vm.Speakers)
                    if (s.SpeakerKey == sourceUid) { sourceName = s.DisplayName; break; }

            if (title != null) title.Text = $"Merge \"{sourceName}\" into:";
            panel.IsVisible = true;
        }

        private void OpenReassignPicker(string sourceUid, SpeakerSegmentSlice slice)
        {
            _pendingMergeSourceUid = sourceUid;
            _pendingReassignSlice = slice;
            _pendingReassignSourceUid = sourceUid;

            var panel = this.Find<Border>("MergePickerPanel");
            var title = this.Find<TextBlock>("MergePickerTitle");
            if (panel == null) return;

            if (title != null) title.Text = $"Reassign {slice.TimeLabel} to:";
            panel.IsVisible = true;
        }

        private void CloseMergePicker()
        {
            _pendingMergeSourceUid = null;
            _pendingReassignSlice = null;
            _pendingReassignSourceUid = null;
            var panel = this.Find<Border>("MergePickerPanel");
            if (panel != null) panel.IsVisible = false;
        }

        private async void OnMergeTargetClick(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Button btn || btn.Name != "MergeTargetBtn") return;
            if (btn.Tag is not string targetUid) return;

            var vm = DataContext as NavPaneViewModel;
            if (vm == null) { CloseMergePicker(); return; }

            if (_pendingReassignSlice != null && _pendingReassignSourceUid != null)
            {
                // Reassign segment flow
                if (vm.SegmentReassignRequested != null)
                    await vm.SegmentReassignRequested(
                        _pendingReassignSourceUid,
                        _pendingReassignSlice.StartSec,
                        _pendingReassignSlice.EndSec,
                        targetUid);
            }
            else if (_pendingMergeSourceUid != null && targetUid != _pendingMergeSourceUid)
            {
                // Merge speakers flow
                if (vm.SpeakerMergeRequested != null)
                    await vm.SpeakerMergeRequested(_pendingMergeSourceUid, targetUid);
            }

            CloseMergePicker();
            e.Handled = true;
        }

        // ─── Merge suggestion handlers ────────────────────────────────────────

        private async void OnSuggestionButtonClick(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Button btn) return;
            var vm = DataContext as NavPaneViewModel;
            if (vm == null) return;

            if (btn.Name == "AcceptSuggestionBtn" && btn.Tag is MergeSuggestion accept)
            {
                // Merge uid1 into uid2
                if (vm.SpeakerMergeRequested != null)
                    await vm.SpeakerMergeRequested(accept.Uid1, accept.Uid2);
                vm.MergeSuggestions.Remove(accept);
                vm.OnPropertyChanged(nameof(NavPaneViewModel.HasMergeSuggestions));
                e.Handled = true;
            }
            else if (btn.Name == "DismissSuggestionBtn" && btn.Tag is MergeSuggestion dismiss)
            {
                if (vm.MergeSuggestionDismissRequested != null)
                    await vm.MergeSuggestionDismissRequested(dismiss.Pid1, dismiss.Pid2);
                vm.MergeSuggestions.Remove(dismiss);
                vm.OnPropertyChanged(nameof(NavPaneViewModel.HasMergeSuggestions));
                e.Handled = true;
            }
        }

        // ─── P3.5: Open preferences ──────────────────────────────────────────

        private void OpenPreferencesBtn_Click(object? sender, RoutedEventArgs e)
        {
            var mainWindow = this.GetVisualRoot() as m_mslc_overlay.MainWindow;
            if (mainWindow != null)
            {
                var prefs = new m_mslc_overlay.views.dialogs.PreferencesDialog();
                prefs.ShowDialog(mainWindow);
            }
        }

        // ─── Helper: walk visual tree to find ancestor DataContext ───────────

        private static T? FindAncestorDataContext<T>(Control start) where T : class
        {
            Visual? current = start;
            while (current != null)
            {
                if (current is StyledElement se && se.DataContext is T match)
                    return match;
                current = current.GetVisualParent();
            }
            return null;
        }
    }
}
