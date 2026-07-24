using Xunit;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Core.Workspace.Tests;

public class UndoRedoStackTests
{
    [Fact]
    public void PushUndoRedo_WorksAsExpected()
    {
        // Arrange
        var stack = new UndoRedoStack();
        var evt1 = new PatchEvent { Id = 1, ValueNew = "A" };
        var evt2 = new PatchEvent { Id = 2, ValueNew = "B" };
        
        // Act
        stack.PushEdit(evt1);
        stack.PushEdit(evt2);
        
        var undo1 = stack.Undo(); // B
        var undo2 = stack.Undo(); // A
        var undo3 = stack.Undo(); // null

        var redo1 = stack.Redo(); // A
        
        // Assert
        Assert.Equal(2, undo1?.Id);
        Assert.Equal(1, undo2?.Id);
        Assert.Null(undo3);
        
        Assert.Equal(1, redo1?.Id);
    }
    
    [Fact]
    public void PushEdit_ClearsRedoStack()
    {
        // Arrange
        var stack = new UndoRedoStack();
        var evt1 = new PatchEvent { Id = 1 };
        var evt2 = new PatchEvent { Id = 2 };
        var evt3 = new PatchEvent { Id = 3 };
        
        stack.PushEdit(evt1);
        stack.PushEdit(evt2);
        stack.Undo(); // pop evt2 to redo stack
        
        // Act
        stack.PushEdit(evt3); // This should clear redo stack
        
        var redo = stack.Redo();
        
        // Assert
        Assert.Null(redo); // Redo stack was cleared
    }
}
