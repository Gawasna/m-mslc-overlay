using System.Collections.Generic;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.ViewModels.Workspace;

public class UndoRedoStack
{
    private readonly Stack<PatchEvent> _undoStack = new();
    private readonly Stack<PatchEvent> _redoStack = new();

    public void PushEdit(PatchEvent evt)
    {
        _undoStack.Push(evt);
        _redoStack.Clear(); 
    }

    public PatchEvent? Undo()
    {
        if (_undoStack.Count == 0) return null;
        var evt = _undoStack.Pop();
        _redoStack.Push(evt);
        return evt;
    }

    public PatchEvent? Redo()
    {
        if (_redoStack.Count == 0) return null;
        var evt = _redoStack.Pop();
        _undoStack.Push(evt);
        return evt;
    }
}
