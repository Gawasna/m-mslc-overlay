using System;
using System.Collections.Generic;
using System.Linq;

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

        // speech pacing (ATOM1)
        private readonly List<double> _speechSpeedHistory = new List<double>();
        private const double DefaultAvgSS = 330.0; // ms/word mặc định
        private DateTime _lastActivityTime = DateTime.MinValue;

        // Non-boundary Endings (Lightweight Syntactic Filter for ATOM29)
        private static readonly HashSet<string> NonBoundaryEndings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Giới từ (Prepositions)
            "to", "in", "into", "on", "at", "of", "for", "with", "from", "by", "about", "through", "under", "over", "like",
            // Liên từ (Conjunctions)
            "and", "but", "or", "so", "because", "although", "if", "when", "while", "as", "than",
            // Mạo từ & Từ hạn định (Articles & Determiners)
            "a", "an", "the", "this", "that", "these", "those", "my", "your", "his", "her", "its", "our", "their",
            // Động từ khuyết thiếu & Trợ động từ ở cuối (Auxiliaries & Tobe)
            "is", "are", "am", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did", 
            "will", "would", "should", "can", "could", "may", "might"
        };

        // ── public config ─────────────────────────────────────────────────
        /// <summary>
        /// Milliseconds to wait after the first isFinal before committing.
        /// Absorbs Azure late-correction double-final. Calibrate from log.
        /// </summary>
        public int FinalGateMs { get; set; } = 300;

        /// <summary>
        /// Tham số thời gian ngắt A4 (ms) cộng thêm vào AvgSS.
        /// </summary>
        public int SilenceParamMs { get; set; } = 800;

        // ── public surface (kept identical for existing callers) ──────────
        public int SentenceIndex { get; private set; } = 0;

        public double AverageSpeechSpeed 
        {
            get 
            {
                lock (_speechSpeedHistory)
                {
                    return _speechSpeedHistory.Count > 0 ? _speechSpeedHistory.Average() : DefaultAvgSS;
                }
            }
        }

        public void Reset()
        {
            _prevText = "";
            _confirmedLen = 0;
            _pendingFinal = null;
            _pendingFinalTime = DateTime.MinValue;
            _emittedSentences.Clear();
            _lastOffset = 0;
            _lastActivityTime = DateTime.MinValue;
            lock (_speechSpeedHistory)
            {
                _speechSpeedHistory.Clear();
            }
        }

        /// <summary>
        /// Cập nhật nhịp độ nói trung bình dựa trên duration của partial
        /// </summary>
        public void UpdatePacing(string text, ulong duration)
        {
            if (string.IsNullOrWhiteSpace(text) || duration == 0) return;

            // Đổi duration từ 100-nanoseconds ticks sang ms
            double ms = duration / 10000.0;
            
            // Tính số từ
            int wordCount = CountWords(text);
            if (wordCount < 3) return;

            double msPerWord = ms / wordCount;

            // Lọc các giá trị dị biệt
            if (msPerWord >= 100 && msPerWord <= 1500)
            {
                lock (_speechSpeedHistory)
                {
                    _speechSpeedHistory.Add(msPerWord);
                    if (_speechSpeedHistory.Count > 15) // Giữ 15 giá trị gần nhất
                    {
                        _speechSpeedHistory.RemoveAt(0);
                    }
                }
            }
        }

        /// <summary>
        /// Call on every text packet received from the pipe.
        /// Returns zero or more newly-committed sentences with their reasons.
        /// </summary>
        public List<(string Text, string Reason)> ExtractNewSentences(string text, bool isFinal, ulong offset = 0, ulong duration = 0)
        {
            var results = new List<(string Text, string Reason)>();

            // Cập nhật Pacing
            if (!isFinal && duration > 0)
            {
                UpdatePacing(text, duration);
            }

            // Ghi nhận thời điểm nhận partial text mới thay đổi để tính silence gap (A4)
            if (!string.IsNullOrEmpty(text) && text != _prevText)
            {
                _lastActivityTime = DateTime.UtcNow;
            }

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
                            TryEmit(tail, "OffsetChange", results);
                        }
                    }
                }
                ResetForNextUtterance();
                _pendingFinal = null;
                _lastActivityTime = DateTime.MinValue;
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

                _lastActivityTime = DateTime.MinValue; // reset silence timer on final
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
                        TryEmit(sentence, "Boundary", results);
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
        /// Flushes gated finals whose hold period has elapsed, and checks for A4 silence commit.
        /// </summary>
        public List<(string Text, string Reason)> Tick()
        {
            var results = new List<(string Text, string Reason)>();

            // 1. Gated final commit
            if (_pendingFinal != null &&
                (DateTime.UtcNow - _pendingFinalTime).TotalMilliseconds >= FinalGateMs)
            {
                TryEmit(_pendingFinal, "AzureFinal", results);
                _pendingFinal = null;
                ResetForNextUtterance();
                _lastActivityTime = DateTime.MinValue;
            }

            // 2. A4 Commit (Silence Gap / Expect COMMIT By A4)
            if (_prevText.Length > _confirmedLen && _lastActivityTime != DateTime.MinValue)
            {
                double silenceDuration = (DateTime.UtcNow - _lastActivityTime).TotalMilliseconds;
                var tail = _prevText.Substring(_confirmedLen).Trim();
                
                double threshold = AverageSpeechSpeed + SilenceParamMs;
                if (IsUnsafeToCommit(tail))
                {
                    // Nếu không an toàn (kết thúc bằng giới từ/liên từ hoặc quá ngắn), nhân phạt ngưỡng im lặng
                    threshold = Math.Max(threshold * 2.5, 2500.0);
                }

                if (silenceDuration >= threshold)
                {
                    if (!string.IsNullOrWhiteSpace(tail))
                    {
                        TryEmit(tail, "A4", results);
                        _confirmedLen = _prevText.Length; // chốt phần này
                    }
                    _lastActivityTime = DateTime.MinValue; // reset để tránh lặp lại cho đến khi có partial mới
                }
            }

            return results;
        }

        // ── helpers ───────────────────────────────────────────────────────

        private bool IsUnsafeToCommit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            string[] words = text.Split(new[] { ' ', '.', ',', '?', '!' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return true;

            string lastWord = words[^1].Trim();

            // Nếu từ cuối cùng nằm trong danh mục từ cấm ngắt câu
            if (NonBoundaryEndings.Contains(lastWord))
            {
                return true;
            }

            // Ràng buộc độ dài tối thiểu: câu dưới 3 từ rất dễ bị đứt đoạn ngữ nghĩa
            if (words.Length < 3)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Emit only if this sentence has not been emitted before.
        /// Returns true if actually emitted.
        /// </summary>
        private bool TryEmit(string sentence, string reason, List<(string Text, string Reason)> results)
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
            results.Add((normalized, reason)); // Emit the original with punctuation
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

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            int count = 0;
            bool wasSpace = true;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    wasSpace = true;
                }
                else
                {
                    if (wasSpace)
                    {
                        count++;
                        wasSpace = false;
                    }
                }
            }
            return count;
        }
    }
}