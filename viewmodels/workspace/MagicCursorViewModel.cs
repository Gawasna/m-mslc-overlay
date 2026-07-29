using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MMslcOverlay.ViewModels.Workspace;

public enum MagicCursorState
{
    Idle,
    Active,
    Waiting
}

public class MagicCursorViewModel : INotifyPropertyChanged
{
    private readonly Func<int> _getAnchorOffset;
    private MagicCursorState _state = MagicCursorState.Idle;

    public MagicCursorViewModel(Func<int> getAnchorOffset)
    {
        _getAnchorOffset = getAnchorOffset;
    }

    public int Offset => _getAnchorOffset();

    public MagicCursorState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
