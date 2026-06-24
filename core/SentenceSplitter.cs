using System;
using System.Collections.Generic;

namespace m_mslc_overlay.core
{
    public class SentenceSplitter
    {
        // ── internal state ────────────────────────────────────────────────
        private string _prevText = "";
        private int _confirmedLen = 0;
        private ulong _lastOffset = 0;

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
            _lastOffset = 0;
            // SentenceIndex intentionally NOT reset — monotonically increasing
        }

        /// <summary>
        /// Call on every text packet received from the pipe.
        /// Returns zero or more newly-committed sentences.
        /// </summary>
        public List<string> ExtractNewSentences(string text, bool isFinal, ulong offset = 0)
        {
            var results = new List<string>();

            // Force emit unconfirmed tail if offset changes (Prevents lost/skipped sentences)
            if (offset != 0 && _lastOffset != 0 && offset != _lastOffset)
            {
                if (_confirmedLen < _prevText.Length)
                {
                    var tail = _prevText.Substring(_confirmedLen).TrimStart();
                    if (!string.IsNullOrWhiteSpace(tail))
                    {
                        // Smart filtering: Avoid emitting fragments that Azure simply carried over to the new offset
                        if (string.IsNullOrEmpty(text) || !text.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
                        {
                            TryEmit(tail, results);
                        }
                    }
                }
                ResetForNextUtterance();
                _pendingFinal = null;
            }
            if (offset != 0) _lastOffset = offset;

            if (string.IsNullOrEmpty(text))
                return results;

            // ── regression guard (ATOM2 generalized LCP) ─────────
            if (!string.IsNullOrEmpty(_prevText))
            {
                int commonLen = CommonPrefixLength(text, _prevText);
                if (commonLen < _prevText.Length)
                {
                    if (commonLen < _confirmedLen)
                    {
                        _confirmedLen = commonLen;
                    }
                }
            }
            else if (text.Length < _confirmedLen)
            {
                _confirmedLen = text.Length;
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

            // Chống nhiễu: Lọc các câu chỉ toàn dấu câu (VD: ".")
            bool hasAlnum = false;
            foreach (char c in sentence)
            {
                if (char.IsLetterOrDigit(c))
                {
                    hasAlnum = true;
                    break;
                }
            }
            if (!hasAlnum)
                return false;

            var normalized = sentence.Trim();
            
            // Chống lặp câu (Punctuation-insensitive deduplication)
            // Khử trùng lặp giữa "sentence" và "sentence."
            var stripped = new System.Text.StringBuilder();
            foreach (char c in normalized)
            {
                if (!char.IsPunctuation(c))
                    stripped.Append(char.ToLowerInvariant(c));
            }
            var dedupKey = stripped.ToString().Trim();

            if (_emittedSentences.Contains(dedupKey))
                return false;

            _emittedSentences.Add(dedupKey);
            results.Add(normalized); // Emit the original with punctuation
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