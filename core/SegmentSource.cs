namespace m_mslc_overlay.core
{
    /// <summary>
    /// Identifies whether a transcript segment was produced by an automated
    /// machine pipeline (LiveCaption) or typed/entered by a human operator.
    /// Drives visual differentiation in PaperSheet:
    ///   Machine → bold text, orange left-border accent
    ///   Human   → italic + underlined text, blue left-border accent
    /// </summary>
    public enum SegmentSource
    {
        Machine,
        Human
    }
}
