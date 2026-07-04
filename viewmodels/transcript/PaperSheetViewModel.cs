using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using m_mslc_overlay.core;

namespace m_mslc_overlay.viewmodels.transcript
{
    // ─── Supporting models ─────────────────────────────────────────────────────

    /// <summary>
    /// Represents a single rendered bilingual segment displayed on the paper sheet.
    /// Immutable after construction — mutations create new instances.
    /// </summary>
    public sealed class TranscriptSegmentItem
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string SpeakerLabel { get; init; } = string.Empty;
        public string OriginalText { get; init; } = string.Empty;
        public string TranslatedText { get; init; } = string.Empty;
        public TimeSpan Timestamp { get; init; }
        public SegmentState State { get; init; } = SegmentState.Committed;
        public bool IsActive { get; init; }

        public string TimestampFormatted => Timestamp.ToString(@"hh\:mm\:ss");
        public bool HasTranslation => !string.IsNullOrEmpty(TranslatedText);
    }

    /// <summary>
    /// Speaker annotation entry used by the nav pane Speaker Annotation state.
    /// </summary>
    public sealed class SpeakerAnnotation : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;

        public string SpeakerKey { get; init; } = string.Empty; // e.g. "SPEAKER 1"

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─── ViewModel ────────────────────────────────────────────────────────────

    /// <summary>
    /// Manages the paper sheet content: ordered bilingual segments, active segment
    /// highlight, auto-scroll flag, and zoom level.
    /// Does NOT interact with AI or pipe services — receives data via Push() method.
    /// </summary>
    public sealed class PaperSheetViewModel : INotifyPropertyChanged
    {
        // ─── Segments ──────────────────────────────────────────────────────

        public ObservableCollection<TranscriptSegmentItem> Segments { get; } = new();

        private TranscriptSegmentItem? _activeSegment;
        public TranscriptSegmentItem? ActiveSegment
        {
            get => _activeSegment;
            private set { _activeSegment = value; OnPropertyChanged(); }
        }

        // ─── Speakers ─────────────────────────────────────────────────────

        public ObservableCollection<SpeakerAnnotation> Speakers { get; } = new();

        // ─── Display options ──────────────────────────────────────────────

        private bool _autoScroll = true;
        public bool AutoScroll
        {
            get => _autoScroll;
            set { _autoScroll = value; OnPropertyChanged(); }
        }

        private bool _focusMode;
        public bool FocusMode
        {
            get => _focusMode;
            set { _focusMode = value; OnPropertyChanged(); }
        }

        private double _zoomLevel = 1.0; // 0.5 – 2.0
        public double ZoomLevel
        {
            get => _zoomLevel;
            set { _zoomLevel = Math.Clamp(value, 0.5, 2.0); OnPropertyChanged(); OnPropertyChanged(nameof(ZoomPercent)); }
        }

        public int ZoomPercent => (int)(_zoomLevel * 100);

        // ─── Document metadata ────────────────────────────────────────────

        public int PageNumber => 1; // Static for now; multi-page out of scope
        public int WordCount { get; private set; }
        public string DocumentLanguage => "English (U.S.)";

        // ─── Public API ───────────────────────────────────────────────────

        /// <summary>
        /// Adds a new committed segment. Segments without translation show as pending.
        /// </summary>
        public void PushSegment(TranscriptSegmentItem segment)
        {
            Segments.Add(segment);
            SetActive(segment.Id);
            RecalcWordCount();

            // Auto-register speaker if new
            if (!string.IsNullOrEmpty(segment.SpeakerLabel) && !HasSpeaker(segment.SpeakerLabel))
            {
                Speakers.Add(new SpeakerAnnotation
                {
                    SpeakerKey = segment.SpeakerLabel,
                    DisplayName = ExtractDisplayName(segment.SpeakerLabel)
                });
            }
        }

        /// <summary>
        /// Updates the translated text of an existing segment (immutable swap).
        /// </summary>
        public void UpdateTranslation(Guid segmentId, string translatedText)
        {
            for (int i = 0; i < Segments.Count; i++)
            {
                if (Segments[i].Id == segmentId)
                {
                    var updated = new TranscriptSegmentItem
                    {
                        Id = Segments[i].Id,
                        SpeakerLabel = Segments[i].SpeakerLabel,
                        OriginalText = Segments[i].OriginalText,
                        TranslatedText = translatedText,
                        Timestamp = Segments[i].Timestamp,
                        State = SegmentState.Translated,
                        IsActive = Segments[i].IsActive
                    };
                    Segments[i] = updated;
                    return;
                }
            }
        }

        public void ZoomIn() => ZoomLevel = Math.Round(ZoomLevel + 0.1, 1);
        public void ZoomOut() => ZoomLevel = Math.Round(ZoomLevel - 0.1, 1);
        public void ZoomReset() => ZoomLevel = 1.0;

        public void Clear()
        {
            Segments.Clear();
            Speakers.Clear();
            ActiveSegment = null;
            WordCount = 0;
            OnPropertyChanged(nameof(WordCount));
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        private void SetActive(Guid id)
        {
            // Rebuild active segment reference (items are immutable)
            for (int i = 0; i < Segments.Count; i++)
            {
                if (Segments[i].Id == id)
                {
                    ActiveSegment = Segments[i];
                    return;
                }
            }
        }

        private bool HasSpeaker(string key)
        {
            foreach (var s in Speakers)
                if (s.SpeakerKey == key) return true;
            return false;
        }

        private void RecalcWordCount()
        {
            int total = 0;
            foreach (var seg in Segments)
                total += seg.OriginalText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            WordCount = total;
            OnPropertyChanged(nameof(WordCount));
        }

        private static string ExtractDisplayName(string speakerKey)
        {
            // "SPEAKER 1" -> "Speaker 1"
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(speakerKey.ToLower());
        }

        // ─── INotifyPropertyChanged ──────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
