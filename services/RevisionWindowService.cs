using System;
using System.Threading;
using m_mslc_overlay.core;

namespace m_mslc_overlay.services
{
    /// <summary>
    /// ATOM80 — RevisionWindow Protocol.
    ///
    /// After a short SoftCommit (≤3 words, IsDangling or WordCount&lt;=3) is translated,
    /// opens a 400ms window. If the next translation arrives within the window and
    /// the combined text makes semantic sense, fires OnRevise(previousTranslation,
    /// mergedTranslation) instead of appending a new entry.
    ///
    /// This prevents "nhưng" rendering alone when "but this is the 100,000..." arrives
    /// shortly after: the overlay hot-replaces "nhưng" with "nhưng đây là..." instead.
    /// </summary>
    public class RevisionWindowService : IDisposable
    {
        /// Grace period in milliseconds after a short translation renders.
        public int WindowMs { get; set; } = 3000;

        /// Fired when a merge opportunity is detected.
        /// Args: (previousTranslation, mergedTranslation)
        public event Action<string, string>? OnRevise;

        private readonly object _lock = new();
        private string? _pendingTranslation;      // last short translation
        private CommitMetadata? _pendingMeta;     // its source commit
        private Timer? _windowTimer;
        private bool _disposed;

        // ── Public API ────────────────────────────────────────────────────────

        /// Called after every translation is rendered to overlay.
        /// If the translation is from a short/dangling commit, opens the revision window.
        public void OnTranslationRendered(TranslationResult result)
        {
            if (result.Source == null) return;
            bool isShortOrDangling = result.Source.WordCount <= 3 || result.Source.IsDangling;
            if (!isShortOrDangling) return;

            lock (_lock)
            {
                if (_disposed) return;
                // Open revision window for this short translation
                _pendingTranslation = result.Translation;
                _pendingMeta = result.Source;
                StartTimer();
            }
        }

        /// Called when the next translation arrives (OnTranslationCompleted).
        /// If within the window, merge and fire OnRevise; otherwise just reset window.
        public bool TryRevise(TranslationResult nextResult, out string mergedText)
        {
            mergedText = nextResult.Translation;
            lock (_lock)
            {
                if (_disposed || _pendingTranslation == null || _pendingMeta == null)
                    return false;

                // Only revise if the next commit is a longer/different utterance
                // (not from the same utterance offset as the pending one — that would
                // just be a duplicate).
                bool isDifferentUtterance = nextResult.Source == null
                    || nextResult.Source.UtteranceOffset != _pendingMeta.UtteranceOffset
                    || nextResult.Source.Text != _pendingMeta.Text;

                if (!isDifferentUtterance) return false;

                string prev = _pendingTranslation;
                
                // Since the next commit does not contain the previous short text (it was flushed separately),
                // we must concatenate the previous translation with the new translation to preserve both.
                string merged = prev + " " + nextResult.Translation;

                // Fire revision — overlay will replace prev with merged
                ClearPending();
                OnRevise?.Invoke(prev, merged);
                mergedText = merged;
                return true;
            }
        }

        /// Reset on pipe reconnect or overlay clear.
        public void Reset()
        {
            lock (_lock) { ClearPending(); }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                ClearPending();
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void StartTimer()
        {
            _windowTimer?.Dispose();
            _windowTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    // Window expired without a merge — clear pending, let next translation append normally
                    ClearPending();
                }
            }, null, WindowMs, Timeout.Infinite);
        }

        private void ClearPending()
        {
            _pendingTranslation = null;
            _pendingMeta = null;
            _windowTimer?.Dispose();
            _windowTimer = null;
        }
    }
}
