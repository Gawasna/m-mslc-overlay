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
    
    public event System.Action<ScrollMode>? ModeChanged;

    public void SetMode(ScrollMode mode)
    {
        if (CurrentMode != mode)
        {
            CurrentMode = mode;
            ModeChanged?.Invoke(mode);
        }
    }
}
