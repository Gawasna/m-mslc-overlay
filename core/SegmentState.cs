namespace m_mslc_overlay.core
{
    /// <summary>
    /// ATOM79 — Lifecycle states of a caption segment.
    /// COMMITTED  : Text has been locked by the commit engine
    /// TRANSLATED : Translation has been received and linked
    /// RENDERED   : Text/translation has been sent to the overlay
    /// STALE      : Overlay was cleared/reset; segment is no longer visible
    /// </summary>
    public enum SegmentState
    {
        Committed,
        Translated,
        Rendered,
        Stale
    }
}
