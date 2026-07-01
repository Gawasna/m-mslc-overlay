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

        public AdaptiveCommitEngine(string languageCode = "en", int minWordThreshold = 6, int lookaheadSafetyWindow = 2)
        {
            _languageCode = languageCode.ToLower();
            _minWordThreshold = minWordThreshold;
            _lookaheadSafetyWindow = lookaheadSafetyWindow;
        }

        public CommitResult Evaluate(string currentText, double timestampMs, bool isFinalFromSdk)
        {
            var result = new CommitResult();
            
            if (isFinalFromSdk)
            {
                string committed = ExtractRemaining(currentText);
                ResetState();
                result.Type = CommitType.Hard;
                result.CommittedText = committed;
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
            _lastWordTimestampMs = timestampMs;

            // Update stats only when a single word is added to avoid undercounting 
            // from multi-word packets emitted concurrently by the engine.
            if (currWords.Count - prevWords.Count == 1 && previousWordTimestamp > 0)
            {
                double gap = timestampMs - previousWordTimestamp;
                UpdateGapStatistics(gap);
            }

            // Cancel any pending commit if new words arrive and break the pause
            if (_isPendingCommit && currWords.Count > prevWords.Count)
            {
                _isPendingCommit = false;
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
                double currentIdleTime = timestampMs - previousWordTimestamp;

                if (currentIdleTime > dynamicThreshold && stableWordCount >= _minWordThreshold)
                {
                    // Trigger Pending Commit State (Debounce mechanism)
                    if (!_isPendingCommit)
                    {
                        _isPendingCommit = true;
                        _pendingTimestampMs = timestampMs;
                        _pendingTargetWordIndex = stableWordCount;
                    }
                }
            }

            // Check if Debounce Timer expired for pending commit
            if (_isPendingCommit && (timestampMs - _pendingTimestampMs >= _debounceDelayMs))
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
            _wordGaps.Clear();
        }
    }
}
