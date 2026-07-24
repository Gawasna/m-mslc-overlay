using System;
using System.IO;
using System.Linq;
using Xunit;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;

namespace MMslcOverlay.Core.Workspace.Tests;

public class UserDataRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly UserDataRepository _repo;

    public UserDataRepositoryTests()
    {
        _dbPath = Path.GetTempFileName();
        _repo = new UserDataRepository(_dbPath);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
                // Xóa cả file -wal và -shm nếu có
                if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
                if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
            }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public void InsertAndRetrievePatchEvents_PreservesOrder()
    {
        // Arrange
        var evt1 = new PatchEvent
        {
            EventType = "PATCH",
            SegmentRef = "seg_001:42",
            Field = "text_src",
            ValueOld = "Hello",
            ValueNew = "Hello World",
            CreatedAt = 1000
        };

        var evt2 = new PatchEvent
        {
            EventType = "PATCH",
            SegmentRef = "seg_001:42",
            Field = "text_src",
            ValueOld = "Hello World",
            ValueNew = "Hello World!",
            CreatedAt = 2000
        };

        // Act
        _repo.InsertPatchEvent(evt1);
        _repo.InsertPatchEvent(evt2);

        var events = _repo.GetAllPatchEvents();

        // Assert
        Assert.Equal(2, events.Count);
        Assert.Equal(evt1.Id, events[0].Id);
        Assert.Equal("Hello", events[0].ValueOld);
        Assert.Equal(evt2.Id, events[1].Id);
        Assert.Equal("Hello World!", events[1].ValueNew);
        // Kiểm tra đúng thứ tự CreatedAt
        Assert.True(events[0].CreatedAt < events[1].CreatedAt);
    }

    [Fact]
    public void SaveAndGetUiState_WorksCorrectly()
    {
        // Arrange
        string key = "magic_cursor_anchor";
        string val = "seg_001:100:char_0";

        // Act
        _repo.SaveUiState(key, val, 123456);
        var retrievedVal = _repo.GetUiState(key);

        // Update
        _repo.SaveUiState(key, "seg_002:200:char_5", 123457);
        var updatedVal = _repo.GetUiState(key);

        // Assert
        Assert.Equal(val, retrievedVal);
        Assert.Equal("seg_002:200:char_5", updatedVal);
        Assert.Null(_repo.GetUiState("non_existent_key"));
    }

    [Fact]
    public void InsertAndRetrieveAnnotations()
    {
        // Arrange
        var annotation = new Annotation
        {
            Scope = "SEGMENT",
            SegmentRef = "active:10",
            Type = "HIGHLIGHT",
            Content = null,
            Color = "#FFFF00",
            CreatedAt = 5000
        };

        // Act
        _repo.InsertAnnotation(annotation);
        var annotations = _repo.GetAllAnnotations();

        // Assert
        Assert.Single(annotations);
        Assert.Equal("SEGMENT", annotations[0].Scope);
        Assert.Equal("active:10", annotations[0].SegmentRef);
        Assert.Null(annotations[0].Content);
        Assert.Equal("#FFFF00", annotations[0].Color);
    }

    [Fact]
    public void InsertUpdateAndRetrieveFreeformBlocks()
    {
        // Arrange
        var block = new FreeformBlock
        {
            AnchorAfter = "seg_001:50",
            Content = "This is a note",
            CreatedAt = 100,
            UpdatedAt = 100
        };

        // Act
        _repo.InsertFreeformBlock(block);
        
        block.Content = "This is an updated note";
        block.UpdatedAt = 200;
        _repo.UpdateFreeformBlock(block);

        var blocks = _repo.GetAllFreeformBlocks();

        // Assert
        Assert.Single(blocks);
        Assert.Equal("seg_001:50", blocks[0].AnchorAfter);
        Assert.Equal("This is an updated note", blocks[0].Content);
        Assert.Equal(200, blocks[0].UpdatedAt);
    }
}
