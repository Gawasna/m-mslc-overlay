using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace m_mslc_overlay.viewmodels.transcript
{
    // ─── Nav pane state enum ──────────────────────────────────────────────────

    /// <summary>
    /// The five functional states of the Document Navigation Pane.
    /// Matches the 5-state component design from the wireframe.
    /// </summary>
    public enum NavPaneState
    {
        SpeakerAnnotation,  // Default: label/annotate speakers
        FindReplace,        // Find & Replace text in transcript
        AiSummary,          // Generate AI summary
        AiAutoCorrect,      // AI grammar / style correction
        Glossary            // Term dictionary management
    }

    // ─── Find & Replace state ─────────────────────────────────────────────────

    public sealed class FindReplaceState : INotifyPropertyChanged
    {
        private string _findText = string.Empty;
        private string _replaceText = string.Empty;
        private int _matchCount;
        private string _resultMessage = string.Empty;

        public string FindText
        {
            get => _findText;
            set { _findText = value; OnPropertyChanged(); }
        }

        public string ReplaceText
        {
            get => _replaceText;
            set { _replaceText = value; OnPropertyChanged(); }
        }

        public int MatchCount
        {
            get => _matchCount;
            set { _matchCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResultMessage)); }
        }

        public string ResultMessage => _matchCount > 0 ? $"Found {_matchCount} occurrences." : string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ─── AI state ────────────────────────────────────────────────────────────

    public sealed class AiPaneState : INotifyPropertyChanged
    {
        private string _selectedModel = "Gemini 1.5 Pro";
        private string _summaryText = string.Empty;
        private bool _isBusy;
        private bool _fixSpelling = true;
        private bool _improveStyle = true;
        private string _correctResultMessage = string.Empty;

        public string SelectedModel
        {
            get => _selectedModel;
            set { _selectedModel = value; OnPropertyChanged(); }
        }

        public string SummaryText
        {
            get => _summaryText;
            set { _summaryText = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public bool FixSpelling
        {
            get => _fixSpelling;
            set { _fixSpelling = value; OnPropertyChanged(); }
        }

        public bool ImproveStyle
        {
            get => _improveStyle;
            set { _improveStyle = value; OnPropertyChanged(); }
        }

        public string CorrectResultMessage
        {
            get => _correctResultMessage;
            set { _correctResultMessage = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ─── Glossary state ──────────────────────────────────────────────────────

    public sealed class GlossaryEntry : INotifyPropertyChanged
    {
        private string _term = string.Empty;
        private string _definition = string.Empty;

        public string Term
        {
            get => _term;
            set { _term = value; OnPropertyChanged(); }
        }

        public string Definition
        {
            get => _definition;
            set { _definition = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ─── NavPaneViewModel ─────────────────────────────────────────────────────

    /// <summary>
    /// Orchestrates all five states of the Document Navigation Pane.
    /// Each state is a sub-object; ActiveState controls which panel the view shows.
    /// </summary>
    public sealed class NavPaneViewModel : INotifyPropertyChanged
    {
        private NavPaneState _activeState = NavPaneState.SpeakerAnnotation;
        private bool _isVisible = true;
        private bool _isCompact;

        // ─── Sub-state objects ────────────────────────────────────────────

        public FindReplaceState FindReplace { get; } = new FindReplaceState();
        public AiPaneState AiPane { get; } = new AiPaneState();
        public System.Collections.ObjectModel.ObservableCollection<GlossaryEntry> GlossaryEntries { get; } = new();

        /// <summary>
        /// Speaker list fed from PaperSheetViewModel whenever new speakers are detected.
        /// Updated externally via AddOrUpdateSpeaker().
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<SpeakerAnnotation> Speakers { get; } = new();

        // ─── Active state ─────────────────────────────────────────────────

        public NavPaneState ActiveState
        {
            get => _activeState;
            set
            {
                _activeState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowSpeakerAnnotation));
                OnPropertyChanged(nameof(ShowFindReplace));
                OnPropertyChanged(nameof(ShowAiSummary));
                OnPropertyChanged(nameof(ShowAiAutoCorrect));
                OnPropertyChanged(nameof(ShowGlossary));
            }
        }

        // One boolean per state — used by IsVisible bindings in AXAML
        public bool ShowSpeakerAnnotation => _activeState == NavPaneState.SpeakerAnnotation;
        public bool ShowFindReplace => _activeState == NavPaneState.FindReplace;
        public bool ShowAiSummary => _activeState == NavPaneState.AiSummary;
        public bool ShowAiAutoCorrect => _activeState == NavPaneState.AiAutoCorrect;
        public bool ShowGlossary => _activeState == NavPaneState.Glossary;

        // ─── Pane visibility / layout ─────────────────────────────────────

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(); }
        }

        public bool IsCompact
        {
            get => _isCompact;
            set { _isCompact = value; OnPropertyChanged(); }
        }

        // ─── Commands ─────────────────────────────────────────────────────

        public void SwitchState(NavPaneState state) => ActiveState = state;
        public void Close() => IsVisible = false;
        public void ToggleCompact() => IsCompact = !IsCompact;

        public void AddGlossaryEntry(string term, string definition)
        {
            GlossaryEntries.Add(new GlossaryEntry { Term = term, Definition = definition });
        }

        // ─── INotifyPropertyChanged ──────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
