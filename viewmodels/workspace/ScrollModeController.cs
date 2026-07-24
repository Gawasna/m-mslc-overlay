namespace MMslcOverlay.ViewModels.Workspace;

public enum ScrollMode
{
    FreeInput,
    WatchMagicCursor,
    DoNothing
}

public class ScrollModeController
{
    public ScrollMode CurrentMode { get; private set; } = ScrollMode.FreeInput;

    public void SetMode(ScrollMode mode)
    {
        CurrentMode = mode;
        // Optionally invoke events for UI
    }
}
