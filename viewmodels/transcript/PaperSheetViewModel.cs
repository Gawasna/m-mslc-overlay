using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using m_mslc_overlay.core;
using m_mslc_overlay.viewmodels.transcript;

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

        // ─── Source & per-segment styling ───────────────────────────────────

        /// <summary>Machine (LiveCaption pipe) or Human (manually entered).</summary>
        public SegmentSource Source { get; init; } = SegmentSource.Machine;

        /// <summary>Per-segment bold override. null = use global setting.</summary>
        public bool? IsBoldOverride { get; init; }

        /// <summary>Per-segment italic override. null = use global setting.</summary>
        public bool? IsItalicOverride { get; init; }

        /// <summary>Per-segment underline override. null = use global setting.</summary>
        public bool? IsUnderlinedOverride { get; init; }

        /// <summary>Per-segment font-size override in points. null = use global setting.</summary>
        public double? FontSizeOverride { get; init; }

        public string TimestampFormatted => Timestamp.ToString(@"hh\:mm\:ss");
        public bool HasTranslation => !string.IsNullOrEmpty(TranslatedText);
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

        // ─── Global text formatting (Word-style toolbar) ───────────────────

        private string _globalFontFamily = "Georgia";
        /// <summary>Font family applied to all segments (global). Persisted in config.</summary>
        public string GlobalFontFamily
        {
            get => _globalFontFamily;
            set { _globalFontFamily = value; OnPropertyChanged(); }
        }

        private double _globalFontSize = 11.5;
        /// <summary>Base font size in points (global). Clamped 6–72.</summary>
        public double GlobalFontSize
        {
            get => _globalFontSize;
            set { _globalFontSize = Math.Clamp(value, 6.0, 72.0); OnPropertyChanged(); }
        }

        private bool _globalBold;
        /// <summary>Global bold toggle. Applied to segments unless overridden per-segment.</summary>
        public bool GlobalBold
        {
            get => _globalBold;
            set { _globalBold = value; OnPropertyChanged(); }
        }

        private bool _globalItalic;
        public bool GlobalItalic
        {
            get => _globalItalic;
            set { _globalItalic = value; OnPropertyChanged(); }
        }

        private bool _globalUnderline;
        public bool GlobalUnderline
        {
            get => _globalUnderline;
            set { _globalUnderline = value; OnPropertyChanged(); }
        }

        // ─── Segment source highlight toggles ─────────────────────────────

        private bool _highlightMachineSegments = true;
        /// <summary>Show visual accent (bold + orange border) for Machine segments.</summary>
        public bool HighlightMachineSegments
        {
            get => _highlightMachineSegments;
            set { _highlightMachineSegments = value; OnPropertyChanged(); }
        }

        private bool _highlightHumanSegments = true;
        /// <summary>Show visual accent (italic + underline + blue border) for Human segments.</summary>
        public bool HighlightHumanSegments
        {
            get => _highlightHumanSegments;
            set { _highlightHumanSegments = value; OnPropertyChanged(); }
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
                    var seg = Segments[i];
                    Segments[i] = new TranscriptSegmentItem
                    {
                        Id                   = seg.Id,
                        SpeakerLabel         = seg.SpeakerLabel,
                        OriginalText         = seg.OriginalText,
                        TranslatedText       = translatedText,
                        Timestamp            = seg.Timestamp,
                        State                = SegmentState.Translated,
                        IsActive             = seg.IsActive,
                        Source               = seg.Source,
                        IsBoldOverride       = seg.IsBoldOverride,
                        IsItalicOverride     = seg.IsItalicOverride,
                        IsUnderlinedOverride = seg.IsUnderlinedOverride,
                        FontSizeOverride     = seg.FontSizeOverride
                    };
                    return;
                }
            }
        }

        public void ZoomIn() => ZoomLevel = Math.Round(ZoomLevel + 0.1, 1);
        public void ZoomOut() => ZoomLevel = Math.Round(ZoomLevel - 0.1, 1);
        public void ZoomReset() => ZoomLevel = 1.0;

        // ─── Find & Replace ───────────────────────────────────────────────

        /// <summary>
        /// Replaces all occurrences of <paramref name="findText"/> in segments
        /// that match <paramref name="scope"/>.
        /// Returns (count_changed, result_message).
        /// </summary>
        public (int count, string message) ReplaceAll(
            string findText, string replaceText, ReplaceScope scope)
        {
            if (string.IsNullOrEmpty(findText))
                return (0, "Nothing to replace.");

            int changed = 0;
            for (int i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                bool inScope = scope == ReplaceScope.Both
                    || (scope == ReplaceScope.MachineOnly && seg.Source == SegmentSource.Machine)
                    || (scope == ReplaceScope.HumanOnly  && seg.Source == SegmentSource.Human);

                if (!inScope) continue;
                if (!seg.OriginalText.Contains(findText, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Immutable swap via object initializer
                Segments[i] = new TranscriptSegmentItem
                {
                    Id                = seg.Id,
                    SpeakerLabel      = seg.SpeakerLabel,
                    OriginalText      = seg.OriginalText.Replace(
                                            findText, replaceText,
                                            StringComparison.OrdinalIgnoreCase),
                    TranslatedText    = seg.TranslatedText,
                    Timestamp         = seg.Timestamp,
                    State             = seg.State,
                    IsActive          = seg.IsActive,
                    Source            = seg.Source,
                    IsBoldOverride    = seg.IsBoldOverride,
                    IsItalicOverride  = seg.IsItalicOverride,
                    IsUnderlinedOverride = seg.IsUnderlinedOverride,
                    FontSizeOverride  = seg.FontSizeOverride
                };
                changed++;
            }

            string msg = changed == 0
                ? "No matches found in selected scope."
                : $"Replaced {changed} occurrence(s) successfully.";
            return (changed, msg);
        }

        /// <summary>
        /// Preview how many segments would be affected by a replace operation
        /// without modifying any data. Used to show warning before Replace All.
        /// </summary>
        public int PreviewReplaceCount(string findText, ReplaceScope scope)
        {
            if (string.IsNullOrEmpty(findText)) return 0;
            int count = 0;
            foreach (var seg in Segments)
            {
                bool inScope = scope == ReplaceScope.Both
                    || (scope == ReplaceScope.MachineOnly && seg.Source == SegmentSource.Machine)
                    || (scope == ReplaceScope.HumanOnly  && seg.Source == SegmentSource.Human);
                if (inScope && seg.OriginalText.Contains(findText, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

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
