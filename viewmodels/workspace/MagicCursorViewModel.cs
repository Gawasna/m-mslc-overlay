using System;

namespace MMslcOverlay.ViewModels.Workspace;

public class MagicCursorViewModel
{
    private readonly Func<int> _getAnchorOffset;

    public MagicCursorViewModel(Func<int> getAnchorOffset)
    {
        _getAnchorOffset = getAnchorOffset;
    }

    public int Offset => _getAnchorOffset();

    // Additional logic for dragging, hovering, mode indicating can be added here
}
