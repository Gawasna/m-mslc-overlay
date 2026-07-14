using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using m_mslc_overlay.core;

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
                IsActive = true
            };

            PaperSheet.PushSegment(item);
        }

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
        }
    }
}
