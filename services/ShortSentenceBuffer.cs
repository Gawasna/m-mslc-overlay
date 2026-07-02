using System;
using System.Linq;
using System.Threading;

namespace m_mslc_overlay.services
{
    /// <summary>
    /// ATOM50 — Linguistic Short Sentence Filter.
    ///
    /// Holds commits with word-count <= WordThreshold and merges them with the
    /// next arriving long sentence before forwarding to the translation layer.
    /// This prevents wasteful API/LLM calls for meaningless fragments such as
    /// "but", "So", "Because", "I" that result from Head-2 soft-cue commits.
    ///
    /// Guards implemented:
    ///   • Thread-safe via _bufferLock (Feed/Flush/timer race — Exception 1)
    ///   • Dispose guard to prevent use-after-Stop (Exception 2)
    ///   • Pending word-cap to avoid accumulating too many short fragments (Exception 3)
    ///   • OnFlush wrapped in try-catch (Exception 4)
    ///   • Prefix-overlap detection to avoid "I I upload it…" (S18)
    /// </summary>
    public sealed class ShortSentenceBuffer : IDisposable
    {
        // --- Configuration --------------------------------------------------
        /// Words at or below this count are buffered instead of forwarded immediately.
        public int WordThreshold { get; set; } = 3;

        /// If no long sentence arrives within this window, pending is flushed anyway.
        public int FlushTimeoutMs { get; set; } = 1500;

        /// Maximum pending word count before forcing a flush of the pending alone.
        /// Prevents unbounded accumulation when several short commits arrive without
        /// a long one following (Exception 3).
        public int MaxPendingWords { get; set; } = 6;

        // --- Events ---------------------------------------------------------
        /// Fired with the final (possibly merged) text that should be sent for translation.
        public event Action<string>? OnFlush;

        // --- State ----------------------------------------------------------
        private string _pending = "";
        private readonly object _bufferLock = new object();
        private Timer? _flushTimer;
        private bool _disposed;

        // --------------------------------------------------------------------

        /// <summary>
        /// Feed a new committed segment into the buffer.
        /// </summary>
        /// <param name="text">The committed text from AdaptiveCommitEngine.</param>
        /// <param name="reason">Commit reason string (e.g. "HardCommit", "SoftCommit", "OffsetChange").</param>
        public void Feed(string text, string reason)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (_bufferLock)
            {
                if (_disposed) return;

                // OffsetChange is treated as SoftCommit so ATOM50 can buffer small
                // fragments (e.g. "I up", ".") that arrive after utterance boundaries,
                // rather than force-flushing them individually.
                bool isHard = string.Equals(reason, "HardCommit", StringComparison.OrdinalIgnoreCase);

                int wordCount = CountWords(text);

                if (isHard)
                {
                    // Hard boundary: flush everything immediately regardless of length.
                    string merged = BuildMerged(_pending, text);
                    _pending = "";
                    StopTimer();
                    FireFlush(merged);
                    return;
                }

                if (wordCount <= WordThreshold)
                {
                    // Short fragment — buffer it.
                    // But first check if pending is already too long; if so flush pending alone.
                    int pendingWords = CountWords(_pending);
                    if (pendingWords >= MaxPendingWords && !string.IsNullOrWhiteSpace(_pending))
                    {
                        // Pending has grown too large — flush it standalone before buffering new one.
                        string overflow = _pending;
                        _pending = text.Trim();
                        StopTimer();
                        FireFlush(overflow);
                        // Start fresh timer for new pending.
                        StartTimer();
                    }
                    else
                    {
                        // Append to pending.
                        _pending = string.IsNullOrWhiteSpace(_pending)
                            ? text.Trim()
                            : _pending + " " + text.Trim();

                        // Reset/start the flush-timeout timer.
                        StartTimer();
                    }
                }
                else
                {
                    // Long sentence — merge with any pending and forward.
                    string merged = BuildMerged(_pending, text);
                    _pending = "";
                    StopTimer();
                    FireFlush(merged);
                }
            }
        }

        /// <summary>
        /// Force-flush any pending content immediately (call on session Stop/disconnect).
        /// </summary>
        public void Flush()
        {
            lock (_bufferLock)
            {
                if (_disposed) return;
                StopTimer();
                if (!string.IsNullOrWhiteSpace(_pending))
                {
                    string toFlush = _pending;
                    _pending = "";
                    FireFlush(toFlush);
                }
            }
        }

        /// <summary>
        /// Clear pending state without firing (e.g. on pipe reconnect).
        /// </summary>
        public void Reset()
        {
            lock (_bufferLock)
            {
                StopTimer();
                _pending = "";
            }
        }

        public void Dispose()
        {
            lock (_bufferLock)
            {
                if (_disposed) return;
                _disposed = true;
                StopTimer();
                _pending = "";
            }
        }

        // --- Private helpers ------------------------------------------------

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split(new[] { ' ', '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Length;
        }

        /// <summary>
        /// Merge pending prefix with incoming text.
        /// S18 guard: if <paramref name="incoming"/> already starts with the entire
        /// pending content (word-boundary aware), skip prepend to avoid "I I upload…".
        /// Leading punct guard: strip leading .,;!? from incoming before prepend
        /// to avoid ". Yeah, this is..." when SDK emits a lone punctuation fragment.
        /// </summary>
        private static string BuildMerged(string pending, string incoming)
        {
            string p = pending.Trim();
            // Strip leading punctuation-only fragments from incoming (e.g. "." → "")
            string t = incoming.TrimStart('.', ',', ';', '!', '?', ' ');
            if (string.IsNullOrWhiteSpace(t)) t = incoming.Trim(); // fallback if all stripped

            if (string.IsNullOrEmpty(p))
                return incoming.Trim(); // no pending — return original untrimmed incoming

            // S18 guard — check if 'incoming' already begins with 'pending'
            // at a word boundary to avoid duplicate prefix.
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                int afterPending = p.Length;
                // The character right after the match must be whitespace, punctuation, or EOS —
                // i.e., a word boundary — to avoid false positives like "but" vs "button".
                if (afterPending >= t.Length || !char.IsLetterOrDigit(t[afterPending]))
                    return incoming.Trim(); // pending already absorbed — return incoming as-is
            }

            return p + " " + t;
        }

        private void StartTimer()
        {
            // Reuse existing timer if already running (resets interval).
            if (_flushTimer == null)
            {
                _flushTimer = new Timer(OnTimerElapsed, null,
                    FlushTimeoutMs, Timeout.Infinite);
            }
            else
            {
                _flushTimer.Change(FlushTimeoutMs, Timeout.Infinite);
            }
        }

        private void StopTimer()
        {
            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnTimerElapsed(object? state)
        {
            lock (_bufferLock)
            {
                if (_disposed || string.IsNullOrWhiteSpace(_pending)) return;
                string toFlush = _pending;
                _pending = "";
                StopTimer();
                FireFlush(toFlush);
            }
        }

        private void FireFlush(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                OnFlush?.Invoke(text.Trim());
            }
            catch (Exception ex)
            {
                // Exception 4: OnFlush handler threw — log and swallow to keep buffer alive.
                System.Diagnostics.Debug.WriteLine(
                    $"[ShortSentenceBuffer] OnFlush handler threw: {ex.Message}");
            }
        }
    }
}
