using System.ComponentModel;
using System.Runtime.CompilerServices;
using MMslcOverlay.Services;

namespace MMslcOverlay.ViewModels.Workspace
{
    // ─── Speaker Annotation state ─────────────────────────────────────────────

    /// <summary>
    /// Speaker annotation entry used by the nav pane Speaker Annotation state.
    /// </summary>
    public sealed class SpeakerAnnotation : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;

        public string SpeakerKey { get; init; } = string.Empty; // e.g. UUID uid from atom32

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        /// <summary>Color dot assigned on creation — stable per session.</summary>
        public string ColorHex { get; init; } = "#4E9EF5";

        /// <summary>Recent timeline segments for reassign affordance.</summary>
        public System.Collections.ObjectModel.ObservableCollection<SpeakerSegmentSlice> Segments { get; } = new();

        public bool HasSegments => Segments.Count > 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Represents a single audio segment slice for a speaker (from TimelineUpdateEvent).</summary>
    public sealed class SpeakerSegmentSlice : INotifyPropertyChanged
    {
        public float StartSec { get; init; }
        public float EndSec   { get; init; }

        public string TimeLabel => $"{FormatSec(StartSec)} – {FormatSec(EndSec)}";

        private static string FormatSec(float s)
        {
            int m = (int)(s / 60), sec = (int)(s % 60);
            return m > 0 ? $"{m}:{sec:D2}" : $"{sec}s";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>A merge suggestion pair returned by atom32 get_merge_suggestions.</summary>
    public sealed class MergeSuggestion : INotifyPropertyChanged
    {
        public string Uid1  { get; init; } = string.Empty;
        public string Pid1  { get; init; } = string.Empty;
        public string Name1 { get; init; } = string.Empty;
        public string Uid2  { get; init; } = string.Empty;
        public string Pid2  { get; init; } = string.Empty;
        public string Name2 { get; init; } = string.Empty;
        public float  Dist  { get; init; }

        public string Label => $"{Name1}  ↔  {Name2}  ({Dist:F2})";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

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
        private int _activeMatchIndex;
        private bool _hasSearched;

        public string FindText
        {
            get => _findText;
            set
            {
                _findText = value;
                OnPropertyChanged();
                if (string.IsNullOrWhiteSpace(_findText))
                {
                    ExecuteClearFind();
                }
            }
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

        public int ActiveMatchIndex
        {
            get => _activeMatchIndex;
            set { _activeMatchIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResultMessage)); }
        }

        public bool HasSearched
        {
            get => _hasSearched;
            set { _hasSearched = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResultMessage)); }
        }

        public string ResultMessage
        {
            get
            {
                if (!_hasSearched || string.IsNullOrWhiteSpace(FindText)) return string.Empty;
                if (_matchCount == 0) return "No occurrences found.";
                if (_activeMatchIndex > 0) return $"Match {_activeMatchIndex} of {_matchCount}";
                return $"Found {_matchCount} occurrences.";
            }
        }

        public System.Action<string>? FindNextAction { get; set; }
        public System.Action? ClearFindAction { get; set; }

        public void ExecuteFindNext()
        {
            if (string.IsNullOrWhiteSpace(FindText))
            {
                ExecuteClearFind();
                return;
            }
            HasSearched = true;
            FindNextAction?.Invoke(FindText);
        }

        public void ExecuteClearFind()
        {
            _hasSearched = false;
            _matchCount = 0;
            _activeMatchIndex = 0;
            OnPropertyChanged(nameof(HasSearched));
            OnPropertyChanged(nameof(MatchCount));
            OnPropertyChanged(nameof(ActiveMatchIndex));
            OnPropertyChanged(nameof(ResultMessage));
            ClearFindAction?.Invoke();
        }

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

        // ─── Stable speaker color palette ─────────────────────────────────
        private static readonly string[] SpeakerPalette =
        [
            "#4E9EF5", "#F5A623", "#7ED321", "#BD10E0",
            "#50E3C2", "#F5515F", "#9B59B6", "#1ABC9C",
        ];
        private int _colorIndex = 0;
        private int _unkCounter = 0;

        // ─── Sub-state objects ────────────────────────────────────────────

        public FindReplaceState FindReplace { get; } = new FindReplaceState();
        public AiPaneState AiPane { get; } = new AiPaneState();
        public System.Collections.ObjectModel.ObservableCollection<GlossaryEntry> GlossaryEntries { get; } = new();

        /// <summary>Speaker list fed from diarizer events.</summary>
        public System.Collections.ObjectModel.ObservableCollection<SpeakerAnnotation> Speakers { get; } = new();

        /// <summary>Merge suggestions from atom32 get_merge_suggestions command.</summary>
        public System.Collections.ObjectModel.ObservableCollection<MergeSuggestion> MergeSuggestions { get; } = new();

        // ─── Active state ─────────────────────────────────────────────────

        public NavPaneState ActiveState
        {
            get => _activeState;
            set
            {
                if (_activeState == NavPaneState.FindReplace && value != NavPaneState.FindReplace)
                {
                    FindReplace.ExecuteClearFind();
                }
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
            set
            {
                if (_isVisible && !value && _activeState == NavPaneState.FindReplace)
                {
                    FindReplace.ExecuteClearFind();
                }
                _isVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowSplitter));
            }
        }

        public bool IsCompact
        {
            get => _isCompact;
            set { _isCompact = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowSplitter)); }
        }

        public bool ShowSplitter => _isVisible && !_isCompact;

        // ─── Commands ─────────────────────────────────────────────────────

        public void SwitchState(NavPaneState state) => ActiveState = state;
        public void Close() => IsVisible = false;
        public void ToggleCompact() => IsCompact = !IsCompact;

        public void AddGlossaryEntry(string term, string definition)
        {
            GlossaryEntries.Add(new GlossaryEntry { Term = term, Definition = definition });
        }

        // ─── Speaker management ───────────────────────────────────────────

        /// <summary>
        /// Callbacks wired by MainWindow to forward operations to atom32 IPC.
        /// All are async — MainWindow assigns them after diarizer init.
        /// </summary>
        public System.Func<string, string, System.Threading.Tasks.Task>? SpeakerRenameRequested  { get; set; }
        public System.Func<string, string, System.Threading.Tasks.Task>? SpeakerMergeRequested   { get; set; }
        public System.Func<string, float, float, string, System.Threading.Tasks.Task>? SegmentReassignRequested { get; set; }
        public System.Func<string, string, System.Threading.Tasks.Task>? MergeSuggestionDismissRequested { get; set; }
        public System.Func<System.Threading.Tasks.Task>? RefreshMergeSuggestionsRequested { get; set; }

        /// <summary>
        /// Add or update a speaker in the Speakers collection.
        /// If displayName is empty, assigns UNK# as the default label.
        /// </summary>
        public void AddOrUpdateSpeaker(string speakerKey, string displayName)
        {
            foreach (var s in Speakers)
            {
                if (s.SpeakerKey == speakerKey)
                {
                    // Only update display name if we now have a real identity (not UNK)
                    if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("UNK"))
                        s.DisplayName = displayName;
                    return;
                }
            }

            // New speaker: assign UNK# default if no identity yet
            string effectiveName = string.IsNullOrWhiteSpace(displayName)
                ? $"UNK{++_unkCounter}"
                : displayName;

            string color = SpeakerPalette[_colorIndex % SpeakerPalette.Length];
            _colorIndex++;

            Speakers.Add(new SpeakerAnnotation
            {
                SpeakerKey  = speakerKey,
                DisplayName = effectiveName,
                ColorHex    = color,
            });
        }

        /// <summary>
        /// Sync speakers from diarization timeline segments.
        /// Also updates per-speaker Segments list for reassign affordance.
        /// </summary>
        public void SyncSpeakers(System.Collections.Generic.List<SegmentInfo> segments)
        {
            // Build a lookup of uid → latest segments from timeline
            var byUid = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<SegmentInfo>>();
            foreach (var seg in segments)
            {
                if (!byUid.TryGetValue(seg.Uid, out var list))
                    byUid[seg.Uid] = list = new();
                list.Add(seg);
            }

            foreach (var (uid, segs) in byUid)
            {
                // Ensure speaker entry exists
                var identity = segs[^1].Identity; // use last segment's identity
                AddOrUpdateSpeaker(uid, identity);

                // Update segment slices (keep last 10 for reassign UI)
                var existing = FindSpeaker(uid);
                if (existing == null) continue;
                existing.Segments.Clear();
                int start = System.Math.Max(0, segs.Count - 10);
                for (int i = start; i < segs.Count; i++)
                {
                    existing.Segments.Add(new SpeakerSegmentSlice
                    {
                        StartSec = segs[i].Start,
                        EndSec   = segs[i].End,
                    });
                }
            }
        }

        private SpeakerAnnotation? FindSpeaker(string speakerKey)
        {
            foreach (var s in Speakers)
                if (s.SpeakerKey == speakerKey) return s;
            return null;
        }

        /// <summary>Update merge suggestions list from atom32 response.</summary>
        public void SetMergeSuggestions(System.Collections.Generic.List<MergeSuggestionItem> items)
        {
            MergeSuggestions.Clear();
            foreach (var item in items)
            {
                MergeSuggestions.Add(new MergeSuggestion
                {
                    Uid1  = item.Uid1,
                    Pid1  = item.Pid1,
                    Name1 = item.Name1,
                    Uid2  = item.Uid2,
                    Pid2  = item.Pid2,
                    Name2 = item.Name2,
                    Dist  = item.Dist,
                });
            }
            OnPropertyChanged(nameof(HasMergeSuggestions));
        }

        public bool HasMergeSuggestions => MergeSuggestions.Count > 0;

        // ─── P3.4: Diarizer Availability State ────────────────────────────

        private bool _isDiarizerAvailable = true;
        private string _diarizerUnavailableReason = string.Empty;

        public bool IsDiarizerAvailable
        {
            get => _isDiarizerAvailable;
            private set { _isDiarizerAvailable = value; OnPropertyChanged(); }
        }

        public string DiarizerUnavailableReason
        {
            get => _diarizerUnavailableReason;
            private set { _diarizerUnavailableReason = value; OnPropertyChanged(); }
        }

        public void SetDiarizerUnavailable(string reason)
        {
            IsDiarizerAvailable = false;
            DiarizerUnavailableReason = reason;
        }

        public void SetDiarizerAvailable()
        {
            IsDiarizerAvailable = true;
            DiarizerUnavailableReason = string.Empty;
        }

        // ─── INotifyPropertyChanged ──────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        // internal so views can trigger manual property notifications (e.g. after collection.Remove())
        internal void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
