using System;
using System.Collections.Generic;

namespace m_mslc_overlay.core
{
    public class SentenceSplitter
    {
        // ── internal state ────────────────────────────────────────────────
        private string _prevText = "";
        private int _confirmedLen = 0;

        // time-gated final
        private string? _pendingFinal = null;
        private DateTime _pendingFinalTime = DateTime.MinValue;

        // dedup
        private readonly HashSet<string> _emittedSentences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── public config ─────────────────────────────────────────────────
        /// <summary>
        /// Milliseconds to wait after the first isFinal before committing.
        /// Absorbs Azure late-correction double-final. Calibrate from log.
        /// </summary>
        public int FinalGateMs { get; set; } = 300;

        // ── public surface (kept identical for existing callers) ──────────
        public int SentenceIndex { get; private set; } = 0;

        public void Reset()
        {
            _prevText = "";
            _confirmedLen = 0;
            _pendingFinal = null;
            _pendingFinalTime = DateTime.MinValue;
            _emittedSentences.Clear();
            // SentenceIndex intentionally NOT reset — monotonically increasing
        }

        /// <summary>
        /// Call on every text packet received from the pipe.
        /// Returns zero or more newly-committed sentences.
        /// </summary>
        public List<string> ExtractNewSentences(string text, bool isFinal)
        {
            var results = new List<string>();

            if (string.IsNullOrEmpty(text))
                return results;

            // ── regression guard (smart rollback, not full reset) ─────────
            if (text.Length < _prevText.Length)
            {
                int commonLen = CommonPrefixLength(text, _prevText);

                if (commonLen < _confirmedLen)
                {
                    // correction landed inside already-confirmed region —
                    // must roll back confirmedLen to the common boundary
                    _confirmedLen = commonLen;
                }
                // if correction is entirely after _confirmedLen: nothing to do
            }
            _prevText = text;

            // ── isFinal path ──────────────────────────────────────────────
            if (isFinal)
            {
                var tail = text.Length > _confirmedLen
                    ? text.Substring(_confirmedLen).TrimStart()
                    : "";

                if (!string.IsNullOrEmpty(tail))
                {
                    if (_pendingFinal == null)
                    {
                        // First final for this utterance — gate it
                        _pendingFinal = tail;
                        _pendingFinalTime = DateTime.UtcNow;
                    }
                    else
                    {
                        // Second (correction) final arrived before gate expired —
                        // replace pending with the newer, more accurate version
                        _pendingFinal = tail;
                        _pendingFinalTime = DateTime.UtcNow; // reset the clock
                    }
                }

                return results; // actual emit happens in Tick()
            }

            // ── partial path: scan unconfirmed suffix for punctuation ─────
            int scanPos = _confirmedLen;
            int commitPos = _confirmedLen;

            while (scanPos < text.Length)
            {
                char ch = text[scanPos];
                bool isBoundary = ch == '.' || ch == '?' || ch == '!';

                if (isBoundary)
                {
                    bool atEnd = scanPos + 1 >= text.Length;
                    bool followedBySpace = !atEnd && text[scanPos + 1] == ' ';

                    if (atEnd || followedBySpace)
                    {
                        var sentence = text.Substring(commitPos, scanPos - commitPos + 1).TrimStart();
                        TryEmit(sentence, results);
                        commitPos = scanPos + 1;
                    }
                }
                scanPos++;
            }

            _confirmedLen = commitPos;
            return results;
        }

        /// <summary>
        /// Call from a ~100 ms timer on the UI/worker thread.
        /// Flushes gated finals whose hold period has elapsed.
        /// </summary>
        public List<string> Tick()
        {
            var results = new List<string>();

            if (_pendingFinal != null &&
                (DateTime.UtcNow - _pendingFinalTime).TotalMilliseconds >= FinalGateMs)
            {
                TryEmit(_pendingFinal, results);
                _pendingFinal = null;
                ResetForNextUtterance();
            }

            return results;
        }

        // ── helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Emit only if this sentence has not been emitted before.
        /// Returns true if actually emitted.
        /// </summary>
        private bool TryEmit(string sentence, List<string> results)
        {
            if (string.IsNullOrWhiteSpace(sentence))
                return false;

            var normalized = sentence.Trim();
            if (_emittedSentences.Contains(normalized))
                return false;

            _emittedSentences.Add(normalized);
            results.Add(normalized);
            SentenceIndex++;
            return true;
        }

        /// <summary>
        /// Reset per-utterance state after a final is committed.
        /// Does NOT touch SentenceIndex or _emittedSentences.
        /// </summary>
        private void ResetForNextUtterance()
        {
            _prevText = "";
            _confirmedLen = 0;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
                if (a[i] != b[i]) return i;
            return len;
        }
    }
}