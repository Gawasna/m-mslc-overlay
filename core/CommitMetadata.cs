namespace m_mslc_overlay.core
{
    /// <summary>
    /// ATOM78 — Metadata-enriched Commit Event.
    /// Replaces the bare (string text, string reason) tuple on OnFinalSentenceReceived,
    /// giving downstream layers (translation, overlay, logging) structured context
    /// without re-parsing the committed text.
    /// </summary>
    public class CommitMetadata
    {
        /// The committed text segment.
        public string Text { get; init; } = string.Empty;

        /// Commit origin: "HardCommit", "SoftCommit", "DebounceCommit", "OffsetChange"
        public string Reason { get; init; } = string.Empty;

        /// Acoustic endpoint of the last word in this segment (ms, from SDK offset+duration).
        /// -1 if unavailable (wall-clock fallback was used).
        public double AcousticEndMs { get; init; } = -1.0;

        /// SDK utterance offset (100ns ticks). Useful for OffsetChange tracking.
        public ulong UtteranceOffset { get; init; }

        /// Word count of the committed text.
        public int WordCount { get; init; }

        /// True if Head 3 detected this as a potentially dangling segment
        /// (last word is an open-ended token like "much", "how", "the"...).
        public bool IsDangling { get; init; }

        /// True if ShortSentenceBuffer merged a pending prefix into this text.
        public bool WasMerged { get; init; }

        // Convenience factory
        public static CommitMetadata From(string text, string reason,
            double acousticEndMs = -1, ulong utteranceOffset = 0,
            bool isDangling = false, bool wasMerged = false)
        {
            int wc = string.IsNullOrWhiteSpace(text) ? 0 :
                text.Split(new[] { ' ', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries).Length;
            return new CommitMetadata
            {
                Text = text,
                Reason = reason,
                AcousticEndMs = acousticEndMs,
                UtteranceOffset = utteranceOffset,
                WordCount = wc,
                IsDangling = isDangling,
                WasMerged = wasMerged
            };
        }
    }
}
