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
    ///
    /// FIX V3: Now preserves full CommitMetadata through merge pipeline to prevent
    ///         translation linking drift (UtteranceOffset loss).
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
        /// Fired with the final (possibly merged) CommitMetadata that should be sent for translation.
        /// FIX V3: Changed from Action<string> to Action<CommitMetadata> to preserve UtteranceOffset.
        public event Action<m_mslc_overlay.core.CommitMetadata>? OnFlush;

        // --- State ----------------------------------------------------------
        private string _pending = "";
        private m_mslc_overlay.core.CommitMetadata? _pendingMeta;  // FIX V3: Track metadata
        private readonly object _bufferLock = new object();
        private Timer? _flushTimer;
        private bool _disposed;

        // --------------------------------------------------------------------

        /// <summary>
        /// Feed a new committed segment into the buffer.
        /// FIX V3: Now accepts full CommitMetadata to preserve UtteranceOffset through merges.
        /// </summary>
        /// <param name="meta">The committed segment metadata from AdaptiveCommitEngine.</param>
        public void Feed(m_mslc_overlay.core.CommitMetadata meta)
        {
            if (string.IsNullOrWhiteSpace(meta.Text)) return;

            lock (_bufferLock)
            {
                if (_disposed) return;

                // OffsetChange is treated as SoftCommit so ATOM50 can buffer small
                // fragments (e.g. "I up", ".") that arrive after utterance boundaries,
                // rather than force-flushing them individually.
                bool isHard = string.Equals(meta.Reason, "HardCommit", StringComparison.OrdinalIgnoreCase);

                int wordCount = CountWords(meta.Text);

                if (isHard)
                {
                    // Hard boundary: flush everything immediately regardless of length.
                    var merged = BuildMerged(_pending, _pendingMeta, meta.Text, meta);
                    _pending = "";
                    _pendingMeta = null;
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
                        var overflow = _pendingMeta ?? m_mslc_overlay.core.CommitMetadata.From(_pending, "SoftCommit");
                        _pending = meta.Text.Trim();
                        _pendingMeta = meta;
                        StopTimer();
                        FireFlush(overflow);
                        // Start fresh timer for new pending.
                        StartTimer();
                    }
                    else
                    {
                        // Append to pending.
                        if (string.IsNullOrWhiteSpace(_pending))
                        {
                            _pending = meta.Text.Trim();
                            _pendingMeta = meta;  // Store first metadata
                        }
                        else
                        {
                            _pending = _pending + " " + meta.Text.Trim();
                            // Keep _pendingMeta from first segment (don't overwrite)
                        }

                        // Reset/start the flush-timeout timer.
                        StartTimer();
                    }
                }
                else
                {
                    // Long sentence — merge with any pending and forward.
                    var merged = BuildMerged(_pending, _pendingMeta, meta.Text, meta);
                    _pending = "";
                    _pendingMeta = null;
                    StopTimer();
                    FireFlush(merged);
                }
            }
        }

        /// <summary>
        /// Legacy overload for backward compatibility.
        /// Creates a minimal CommitMetadata and forwards to the main Feed method.
        /// </summary>
        [Obsolete("Use Feed(CommitMetadata) instead. This overload is for backward compatibility only.")]
        public void Feed(string text, string reason)
        {
            Feed(m_mslc_overlay.core.CommitMetadata.From(text, reason));
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
                    var toFlush = _pendingMeta ?? m_mslc_overlay.core.CommitMetadata.From(_pending, "SoftCommit");
                    _pending = "";
                    _pendingMeta = null;
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
                _pendingMeta = null;
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
                _pendingMeta = null;
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
        /// Merge pending prefix with incoming text and metadata.
        /// FIX V3: Returns CommitMetadata with merged text + preserved UtteranceOffset from FIRST segment.
        /// S18 guard: if <paramref name="incomingText"/> already starts with the entire
        /// pending content (word-boundary aware), skip prepend to avoid "I I upload…".
        /// Leading punct guard: strip leading .,;!? from incoming before prepend
        /// to avoid ". Yeah, this is..." when SDK emits a lone punctuation fragment.
        /// </summary>
        private static m_mslc_overlay.core.CommitMetadata BuildMerged(
            string pending, 
            m_mslc_overlay.core.CommitMetadata? pendingMeta,
            string incomingText, 
            m_mslc_overlay.core.CommitMetadata incomingMeta)
        {
            string p = pending.Trim();
            // Strip leading punctuation-only fragments from incoming (e.g. "." → "")
            string t = incomingText.TrimStart('.', ',', ';', '!', '?', ' ');
            if (string.IsNullOrWhiteSpace(t)) t = incomingText.Trim(); // fallback if all stripped

            string mergedText;
            
            if (string.IsNullOrEmpty(p))
            {
                // No pending — return incoming as-is
                mergedText = incomingText.Trim();
                return m_mslc_overlay.core.CommitMetadata.From(
                    mergedText,
                    incomingMeta.Reason,
                    incomingMeta.AcousticEndMs,
                    incomingMeta.UtteranceOffset,  // Use incoming offset
                    incomingMeta.IsDangling,
                    wasMerged: false,
                    incomingMeta.SpeakerId,
                    incomingMeta.SpeakerDisplayName
                );
            }

            // S18 guard — check if 'incoming' already begins with 'pending'
            // at a word boundary to avoid duplicate prefix.
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                int afterPending = p.Length;
                // The character right after the match must be whitespace, punctuation, or EOS —
                // i.e., a word boundary — to avoid false positives like "but" vs "button".
                if (afterPending >= t.Length || !char.IsLetterOrDigit(t[afterPending]))
                {
                    // Pending already absorbed — return incoming as-is but mark as merged
                    mergedText = incomingText.Trim();
                    return m_mslc_overlay.core.CommitMetadata.From(
                        mergedText,
                        incomingMeta.Reason,
                        incomingMeta.AcousticEndMs,
                        pendingMeta?.UtteranceOffset ?? incomingMeta.UtteranceOffset,  // FIX V3: Use FIRST offset
                        incomingMeta.IsDangling,
                        wasMerged: true,
                        pendingMeta?.SpeakerId ?? incomingMeta.SpeakerId,
                        pendingMeta?.SpeakerDisplayName ?? incomingMeta.SpeakerDisplayName
                    );
                }
            }

            // Normal merge: prepend pending to incoming
            mergedText = p + " " + t;
            
            // FIX V3: Preserve metadata from FIRST segment (pendingMeta or incomingMeta)
            var baseMeta = pendingMeta ?? incomingMeta;
            return m_mslc_overlay.core.CommitMetadata.From(
                mergedText,
                incomingMeta.Reason,  // Use incoming reason (more recent)
                incomingMeta.AcousticEndMs,  // Use incoming acoustic end (more recent)
                baseMeta.UtteranceOffset,  // FIX V3: Use FIRST offset for linking
                incomingMeta.IsDangling,  // Use incoming dangling flag (more recent)
                wasMerged: true,
                baseMeta.SpeakerId,  // Use first speaker ID
                baseMeta.SpeakerDisplayName  // Use first speaker name
            );
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
                var toFlush = _pendingMeta ?? m_mslc_overlay.core.CommitMetadata.From(_pending, "SoftCommit");
                _pending = "";
                _pendingMeta = null;
                StopTimer();
                FireFlush(toFlush);
            }
        }

        private void FireFlush(m_mslc_overlay.core.CommitMetadata meta)
        {
            if (string.IsNullOrWhiteSpace(meta.Text)) return;
            try
            {
                OnFlush?.Invoke(meta);
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
