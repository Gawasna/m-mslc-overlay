using System;
using System.Collections.Generic;
using System.Linq;

namespace m_mslc_overlay.core
{
    public enum CommitType
    {
        None,
        Soft,
        Hard
    }

    public class CommitResult
    {
        public CommitType Type { get; set; } = CommitType.None;
        public string CommittedText { get; set; } = string.Empty;
        public string UncommittedText { get; set; } = string.Empty;
        /// ATOM77: true if the last committed word is a dangling open-ended token.
        public bool IsDangling { get; set; } = false;
    }

    public class AdaptiveCommitEngine
    {
        private readonly int _minWordThreshold;
        private readonly int _lookaheadSafetyWindow;
        private readonly string _languageCode;
        private readonly double _debounceDelayMs = 300.0;
        
        private string _lastProcessedText = string.Empty;
        private int _lastCommittedWordIndex = 0;
        
        // Acoustic Gap Statistics (Head 3)
        private readonly List<double> _wordGaps = new();
        private double _lastWordTimestampMs = -1.0;
        private double _runningMeanGap = 350.0; // Default empirical baseline (EN: ~330-360ms)
        private double _runningStDevGap = 200.0;

        // Pending Commit State (Debounce)
        private bool _isPendingCommit = false;
        private double _pendingTimestampMs = 0.0;
        private int _pendingTargetWordIndex = 0;

        // Per-language semantic boundaries
        private static readonly HashSet<string> SoftCuesEn = new(StringComparer.OrdinalIgnoreCase) 
            { "but", "so", "because" };
        
        private static readonly HashSet<string> SoftCuesJa = new(StringComparer.Ordinal) 
            { "は", "が", "を", "か", "ね", "よ", "で", "es", "mas" };

        // ATOM77: Open-ended trailing words — if the last word of a Head-3 candidate
        // is in this set, delay the debounce by one additional cycle to avoid committing
        // incomplete clauses (S20: "how much" without complement).
        private static readonly HashSet<string> DanglingWordsEn = new(StringComparer.OrdinalIgnoreCase)
        {
            "much", "how", "what", "which", "that", "who", "whom", "whose",
            "the", "a", "an", "this", "these", "those", "my", "your", "his", "her", "its", "our", "their",
            "very", "too", "so", "such", "quite", "rather", "more", "less", "most", "least",
            "just", "only", "even", "also", "both", "either", "neither",
            "if", "when", "while", "although", "because", "since", "unless",
            "and", "or", "nor", "not"
        };

        // ATOM77: Tracks whether the current pending commit was already delayed once
        // due to a dangling trailing word. Prevents infinite delay loops.
        private bool _danglingDelayConsumed = false;

        public AdaptiveCommitEngine(string languageCode = "en", int minWordThreshold = 6, int lookaheadSafetyWindow = 2)
        {
            _languageCode = languageCode.ToLower();
            _minWordThreshold = minWordThreshold;
            _lookaheadSafetyWindow = lookaheadSafetyWindow;
        }

        public CommitResult Evaluate(string currentText, double acousticEndMs, double wallClockMs, bool isFinalFromSdk)
        {
            var result = new CommitResult();
            
            if (isFinalFromSdk)
            {
                string committed = ExtractRemaining(currentText);
                ResetState();
                result.Type = CommitType.Hard;
                result.CommittedText = committed;
                result.IsDangling = false; // SDK FINAL is a clean boundary
                return result;
            }

            var prevWords = Tokenize(_lastProcessedText);
            var currWords = Tokenize(currentText);
            
            _lastProcessedText = currentText;

            // Handle ASR Rollback/Mutation (ATOM2 / S5 Punctuation Oscillation)
            // If the engine updates punctuation or retracts words ("nervous." -> "nervous"),
            // rollback handling will clamp the index safely. If a commit had already fired, 
            // the locked text remains locked, preventing trailing modifications.
            if (_lastCommittedWordIndex >= currWords.Count)
            {
                _lastCommittedWordIndex = Math.Max(0, currWords.Count - _lookaheadSafetyWindow);
                _isPendingCommit = false;
            }

            // Find Longest Common Prefix (LCP) - Head 1
            int lcpLength = GetLcpLength(prevWords, currWords);
            int stableWordCount = Math.Max(0, lcpLength - _lookaheadSafetyWindow);
            
            // Gap tracking timing logic & multi-word packet filtering
            // Store previous word timestamp before updating _lastWordTimestampMs
            double previousWordTimestamp = _lastWordTimestampMs;
            _lastWordTimestampMs = acousticEndMs;

            // Update stats only when a single word is added to avoid undercounting 
            // from multi-word packets emitted concurrently by the engine.
            if (currWords.Count - prevWords.Count == 1 && previousWordTimestamp > 0)
            {
                double gap = acousticEndMs - previousWordTimestamp;
                UpdateGapStatistics(gap);
            }

            // Cancel any pending commit if new words arrive and break the pause
            if (_isPendingCommit && currWords.Count > prevWords.Count)
            {
                _isPendingCommit = false;
                _danglingDelayConsumed = false; // ATOM77: reset delay flag on continuation
            }

            if (stableWordCount <= _lastCommittedWordIndex)
            {
                result.Type = CommitType.None;
                result.UncommittedText = currentText;
                return result;
            }

            // Evaluate Head 2: Semantic Boundaries (2-Tier)
            int commitBoundary = -1;
            var softCues = _languageCode == "ja" ? SoftCuesJa : SoftCuesEn;

            for (int i = _lastCommittedWordIndex; i < stableWordCount; i++)
            {
                string word = currWords[i];
                string cleanWord = CleanPunctuation(word);

                // Tier 1: Hard Boundary (always commit immediately, check via punctuation)
                // Rely purely on punctuation matching instead of string equality to avoid done. vs . failures.
                if (HasHardPunctuation(word))
                {
                    commitBoundary = i + 1;
                    _isPendingCommit = false; // Bypass debounce for hard boundaries
                    break;
                }

                // Tier 2: Soft Boundary (commit if stable length >= N_min)
                if (i >= _minWordThreshold && (softCues.Contains(cleanWord) || HasSoftPunctuation(word)))
                {
                    commitBoundary = i + 1;
                }
            }

            // Evaluate Head 3: Adaptive Silence gap detection
            if (commitBoundary == -1 && stableWordCount > _lastCommittedWordIndex)
            {
                // Dynamic threshold calculation based on running statistics (calibrating dynamically)
                double dynamicThreshold = Math.Clamp(_runningMeanGap + 1.5 * _runningStDevGap, 700.0, 1800.0);
                double currentIdleTime = acousticEndMs - previousWordTimestamp;

                if (currentIdleTime > dynamicThreshold && stableWordCount >= _minWordThreshold)
                {
                    // Trigger Pending Commit State (Debounce mechanism)
                    if (!_isPendingCommit)
                    {
                        // ATOM77: Check if the last stable word is a dangling open-ended token.
                        // If so, delay the debounce by one cycle to avoid incomplete clause commits (S20).
                        bool isDanglingCandidate = false;
                        if (!_danglingDelayConsumed && stableWordCount > 0 && stableWordCount <= currWords.Count)
                        {
                            string lastStableWord = CleanPunctuation(currWords[stableWordCount - 1]);
                            isDanglingCandidate = DanglingWordsEn.Contains(lastStableWord);
                        }

                        if (isDanglingCandidate)
                        {
                            // Mark delay consumed — next trigger will proceed regardless of dangling word.
                            _danglingDelayConsumed = true;
                            // Do NOT set _isPendingCommit yet — skip this cycle.
                        }
                        else
                        {
                            _isPendingCommit = true;
                            _pendingTimestampMs = wallClockMs;
                            _pendingTargetWordIndex = stableWordCount;
                            _danglingDelayConsumed = false;
                        }
                    }
                }
            }

            // Check if Debounce Timer expired for pending commit
            if (_isPendingCommit && (wallClockMs - _pendingTimestampMs >= _debounceDelayMs))
            {
                commitBoundary = _pendingTargetWordIndex;
                _isPendingCommit = false;
            }

            if (commitBoundary > _lastCommittedWordIndex)
            {
                var committedWords = currWords.Take(commitBoundary).Skip(_lastCommittedWordIndex);
                result.CommittedText = ReconstructText(committedWords);
                result.UncommittedText = ReconstructText(currWords.Skip(commitBoundary));
                _lastCommittedWordIndex = commitBoundary;
                result.Type = CommitType.Soft;
                // ATOM77: flag if last committed word is dangling (for metadata downstream)
                string lastWord = CleanPunctuation(currWords[commitBoundary - 1]);
                result.IsDangling = DanglingWordsEn.Contains(lastWord);
                _danglingDelayConsumed = false; // reset after actual commit
            }
            else
            {
                result.Type = CommitType.None;
                result.UncommittedText = currentText;
            }

            return result;
        }

        // Active polling fallback for idle streams when no new partial packet arrives.
        // MUST be called by the client UI/Host on a 50-100ms timer loop.
        public CommitResult CheckDebounceTimeout(double currentTimestampMs)
        {
            var result = new CommitResult();
            if (_isPendingCommit && (currentTimestampMs - _pendingTimestampMs >= _debounceDelayMs))
            {
                var currWords = Tokenize(_lastProcessedText);
                int commitBoundary = _pendingTargetWordIndex;
                
                if (commitBoundary > _lastCommittedWordIndex && commitBoundary <= currWords.Count)
                {
                    var committedWords = currWords.Take(commitBoundary).Skip(_lastCommittedWordIndex);
                    result.CommittedText = ReconstructText(committedWords);
                    result.UncommittedText = ReconstructText(currWords.Skip(commitBoundary));
                    _lastCommittedWordIndex = commitBoundary;
                    result.Type = CommitType.Soft;
                }
                _isPendingCommit = false;
            }
            return result;
        }

        private void UpdateGapStatistics(double gap)
        {
            if (gap <= 0 || gap > 5000) return; // Ignore anomalies/long pauses

            _wordGaps.Add(gap);
            if (_wordGaps.Count > 50) _wordGaps.RemoveAt(0); // Keep sliding window of 50 gaps

            _runningMeanGap = _wordGaps.Average();
            double sumOfSquares = _wordGaps.Sum(g => Math.Pow(g - _runningMeanGap, 2));
            _runningStDevGap = Math.Sqrt(sumOfSquares / _wordGaps.Count);
            if (_runningStDevGap < 50) _runningStDevGap = 50; // Set minimum stdev boundary
        }

        private int GetLcpLength(List<string> a, List<string> b)
        {
            int minLen = Math.Min(a.Count, b.Count);
            int i = 0;
            while (i < minLen && a[i].Equals(b[i], StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }
            return i;
        }

        private List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            
            if (_languageCode == "ja")
            {
                // Simple character-based tokenization (Best-effort fallback if MeCab is offline)
                return text.Select(c => c.ToString()).ToList();
            }
            
            return text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private string CleanPunctuation(string word)
        {
            return new string(word.Where(c => !char.IsPunctuation(c)).ToArray());
        }

        private bool HasHardPunctuation(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            char lastChar = word[^1];
            return lastChar == '.' || lastChar == '?' || lastChar == '!' || lastChar == '。';
        }

        private bool HasSoftPunctuation(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            char lastChar = word[^1];
            return lastChar == ',' || lastChar == ';' || lastChar == '，' || lastChar == '；';
        }

        private string ReconstructText(IEnumerable<string> words)
        {
            if (_languageCode == "ja")
            {
                return string.Concat(words);
            }
            return string.Join(" ", words);
        }

        private string ExtractRemaining(string finalProcessedText)
        {
            var words = Tokenize(finalProcessedText);
            if (words.Count > _lastCommittedWordIndex)
            {
                return ReconstructText(words.Skip(_lastCommittedWordIndex));
            }
            return string.Empty;
        }

        public void ResetState()
        {
            _lastProcessedText = string.Empty;
            _lastCommittedWordIndex = 0;
            _lastWordTimestampMs = -1.0;
            _isPendingCommit = false;
            _danglingDelayConsumed = false;
            _wordGaps.Clear();
        }
    }
}
