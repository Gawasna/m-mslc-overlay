using System;
using System.Collections.Generic;

namespace m_mslc_overlay.core
{
    public class SentenceSplitter
    {
        private string _prevText = "";
        private int _confirmedLen = 0;
        public int SentenceIndex { get; private set; } = 0;

        private static readonly char[] Boundaries = { '.', '?', '!' };

        public void Reset()
        {
            _prevText = "";
            _confirmedLen = 0;
            // SentenceIndex không reset - monotonically increasing
        }

        public List<string> ExtractNewSentences(string text, bool isFinal)
        {
            var results = new List<string>();

            // Regression guard
            if (text.Length < _prevText.Length)
            {
                Reset();
            }
            _prevText = text;

            if (isFinal)
            {
                var tail = text.Substring(_confirmedLen).TrimStart();
                if (!string.IsNullOrEmpty(tail))
                    results.Add(tail);
                Reset();
                return results;
            }

            // PARTIAL: scan unconfirmed suffix only
            int scanPos   = _confirmedLen;
            int commitPos = _confirmedLen;

            while (scanPos < text.Length)
            {
                char ch = text[scanPos];
                bool isBoundary = ch == '.' || ch == '?' || ch == '!';

                if (isBoundary)
                {
                    bool atEnd           = scanPos + 1 >= text.Length;
                    bool followedBySpace = !atEnd && text[scanPos + 1] == ' ';

                    if (atEnd || followedBySpace)
                    {
                        var sentence = text.Substring(commitPos, scanPos - commitPos + 1).TrimStart();
                        if (!string.IsNullOrEmpty(sentence))
                            results.Add(sentence);
                        commitPos = scanPos + 1;
                    }
                }
                scanPos++;
            }

            _confirmedLen = commitPos;
            return results;
        }
    }
}
