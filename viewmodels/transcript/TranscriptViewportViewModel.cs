using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using m_mslc_overlay.core;
using m_mslc_overlay.services;

namespace m_mslc_overlay.viewmodels.transcript
{
    /// <summary>
    /// Root ViewModel for the entire Transcript Viewport (Content_Placeholder area).
    /// Owns and wires together the three sub-ViewModels:
    ///   - RecordingSessionViewModel  : header bar recording state
    ///   - PaperSheetViewModel        : A4 paper content and zoom
    ///   - NavPaneViewModel           : left-side 5-state navigation pane
    ///
    /// Acts as the single data entry point — external callers (MainWindow, services)
    /// push data here; this VM fans out to the correct sub-VM.
    /// </summary>
    public sealed class TranscriptViewportViewModel : INotifyPropertyChanged, IDisposable
    {
        // ─── Sub-ViewModels ───────────────────────────────────────────────────

        public RecordingSessionViewModel Recording { get; } = new RecordingSessionViewModel();
        public PaperSheetViewModel PaperSheet { get; } = new PaperSheetViewModel();
        public NavPaneViewModel NavPane { get; } = new NavPaneViewModel();

        // ─── Services ──────────────────────────────────────────────

        private readonly GeminiSummaryService _summaryService;

        // ─── Constructor ──────────────────────────────────────────────

        public TranscriptViewportViewModel()
        {
            _summaryService = new GeminiSummaryService();

            // Route summary results to NavPane AiPane state
            _summaryService.OnSummaryReady += text =>
            {
                NavPane.AiPane.SummaryText = text;
                NavPane.AiPane.IsBusy = false;
                NavPane.AiPane.ErrorMessage = string.Empty;
            };
            _summaryService.OnError += msg =>
            {
                NavPane.AiPane.IsBusy = false;
                NavPane.AiPane.ErrorMessage = msg;
            };
            _summaryService.OnRemainingRequestsChanged += count =>
                NavPane.AiPane.RemainingRequests = count;

            // Wire Find & Replace actions so NavPaneViewModel can call PaperSheet
            NavPane.FindReplace.ReplaceAllAction = (find, replace, scope) =>
                PaperSheet.ReplaceAll(find, replace, scope);
            NavPane.FindReplace.PreviewReplaceCountAction = (find, scope) =>
                PaperSheet.PreviewReplaceCount(find, scope);

            // Activate timer-mode if configured (must run after config is loaded)
            _summaryService.RefreshTimerMode();
        }

        // ─── Layout mode ──────────────────────────────────────────────────────

        private bool _isFullscreen;

        /// <summary>
        /// True when the window is in Fullscreen/Maximized mode.
        /// Drives column-width adjustments visible in TranscriptViewport.axaml.
        /// </summary>
        public bool IsFullscreen
        {
            get => _isFullscreen;
            set { _isFullscreen = value; OnPropertyChanged(); }
        }

        // ─── AI model selection (shared between SubToolbar and AiPane) ─────────

        private string _selectedAiModel = "Gemini 1.5 Pro";
        public string SelectedAiModel
        {
            get => _selectedAiModel;
            set
            {
                _selectedAiModel = value;
                NavPane.AiPane.SelectedModel = value;
                OnPropertyChanged();
            }
        }

        // ─── Speaker tracker label (next speaker to assign) ───────────────────

        private int _nextSpeakerIndex = 1;
        public int NextSpeakerIndex => _nextSpeakerIndex;

        // ─── Data ingestion ───────────────────────────────────────────────────

        /// <summary>
        /// Primary ingestion point: called when a new segment is committed.
        /// Determines speaker assignment and pushes to PaperSheet.
        /// </summary>
        public void PushCommit(CommitMetadata commit, string? speakerOverride = null)
        {
            string speaker = speakerOverride ?? $"SPEAKER {_nextSpeakerIndex}";

            var item = new TranscriptSegmentItem
            {
                SpeakerLabel = speaker,
                OriginalText = commit.Text,
                Timestamp = DateTime.Now.TimeOfDay,
                State = SegmentState.Committed,
                IsActive = true,
                Source = SegmentSource.Machine  // LiveCaption pipe segments are Machine
            };

            PaperSheet.PushSegment(item);
            _summaryService.NotifyNewSegment(commit.Text);
        }

        /// <summary>
        /// Pushes a Human segment (typed manually by operator).
        /// </summary>
        public void PushHumanSegment(string text, string? speakerLabel = null)
        {
            var item = new TranscriptSegmentItem
            {
                SpeakerLabel = speakerLabel ?? "Operator",
                OriginalText = text,
                Timestamp = DateTime.Now.TimeOfDay,
                State = SegmentState.Committed,
                IsActive = true,
                Source = SegmentSource.Human
            };
            PaperSheet.PushSegment(item);
        }

        /// <summary>
        /// Manually request a Gemini summary. Rate-limited to 5/min.
        /// </summary>
        public void RequestSummary()
        {
            NavPane.AiPane.IsBusy = true;
            NavPane.AiPane.ErrorMessage = string.Empty;
            _ = _summaryService.TryRequestSummaryAsync(isAutomatic: false);
        }

        /// <summary>
        /// Re-arms the time-based summary timer when trigger mode or interval changes at runtime.
        /// Safe to call from any thread — delegates to GeminiSummaryService.RefreshTimerMode().
        /// </summary>
        public void RefreshSummaryTimer() => _summaryService.RefreshTimerMode();

        /// <summary>
        /// Called when translation for a segment arrives.
        /// </summary>
        public void PushTranslation(Guid segmentId, string translatedText)
        {
            PaperSheet.UpdateTranslation(segmentId, translatedText);
        }

        /// <summary>
        /// Called when translation for the most recent segment arrives (by index).
        /// Fallback for callers that don't track segment IDs.
        /// </summary>
        public void PushTranslationForLatest(string translatedText)
        {
            var segments = PaperSheet.Segments;
            if (segments.Count == 0) return;
            PaperSheet.UpdateTranslation(segments[^1].Id, translatedText);
        }

        // ─── Lifecycle ────────────────────────────────────────────────────────

        public void StartSession(string? name = null)
        {
            PaperSheet.Clear();
            _nextSpeakerIndex = 1;
            Recording.StartRecording(name ?? $"SESSION #{DateTime.Now:MMdd_HHmm}");

            // Clear context chain so the new session starts fresh
            _summaryService.ResetContext();
            // Restart time-timer if in ByTime mode
            _summaryService.RefreshTimerMode();
        }

        public void StopSession()
        {
            Recording.StopRecording();
        }

        // ─── File operations (stubbed — wired to actual service later) ─────────

        /// <summary>Exports the transcript segments as an SRT subtitle file.</summary>
        public void ExportSrt()
        {
            // TODO: delegate to export service when implemented
        }

        /// <summary>Imports a reference script file into the paper sheet.</summary>
        public void ImportScript()
        {
            // TODO: open file picker and load reference script
        }

        // ─── INotifyPropertyChanged ───────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            Recording.Dispose();
            _summaryService.Dispose();
        }
    }
}
