namespace m_mslc_overlay.core
{
    /// <summary>
    /// ATOM81 — Translation result carrying source CommitMetadata for downstream routing.
    /// Replaces bare string on OnTranslationCompleted, enabling SegmentTracker (ATOM79)
    /// to link translations back to their originating segments.
    /// </summary>
    public class TranslationResult
    {
        /// The translated text.
        public string Translation { get; init; } = string.Empty;

        /// Source commit that triggered this translation. Null for token-streaming completions.
        public CommitMetadata? Source { get; init; }

        /// True if this is an error message rather than a real translation.
        public bool IsError { get; init; }

        public static TranslationResult From(string translation, CommitMetadata? source = null, bool isError = false)
            => new TranslationResult { Translation = translation, Source = source, IsError = isError };
    }
}
