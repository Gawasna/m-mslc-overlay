using System;
using System.IO;
using Xunit;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;

namespace MMslcOverlay.Core.Workspace.Tests;

public class BaseSegmentRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BaseSegmentRepository _repo;

    public BaseSegmentRepositoryTests()
    {
        _dbPath = Path.GetTempFileName();
        _repo = new BaseSegmentRepository(_dbPath);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
                if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
                if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
            }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public void InsertAndGetActiveSegments_FiltersOutSuperseded()
    {
        // Arrange
        var seg1 = new Segment
        {
            TsStartMs = 1000,
            TsEndMs = 2000,
            TextSrc = "Hello",
            CommitType = "PARTIAL",
            ChunkId = "active",
            CreatedAt = 1000
        };

        var seg2 = new Segment
        {
            TsStartMs = 2000,
            TsEndMs = 3000,
            TextSrc = "World",
            CommitType = "HARD",
            ChunkId = "active",
            CreatedAt = 2000
        };

        // Act
        var id1 = _repo.InsertSegment(seg1);
        var id2 = _repo.InsertSegment(seg2);

        // STT update seg1: "Hello" -> "Hello everyone"
        var seg1Update = new Segment
        {
            TsStartMs = 1000,
            TsEndMs = 2100,
            TextSrc = "Hello everyone",
            CommitType = "HARD",
            SupersedesId = id1,
            ChunkId = "active",
            CreatedAt = 2500
        };
        var id1Update = _repo.InsertSegment(seg1Update);

        var activeSegments = _repo.GetActiveSegments();

        // Assert
        Assert.Equal(2, activeSegments.Count); // Should only have seg1Update and seg2
        
        Assert.Equal(id1Update, activeSegments[0].Id);
        Assert.Equal("Hello everyone", activeSegments[0].TextSrc);
        
        Assert.Equal(id2, activeSegments[1].Id);
        Assert.Equal("World", activeSegments[1].TextSrc);
    }

    [Fact]
    public void GetActiveSegments_ReturnsSortedByTsStartMs()
    {
        // Arrange
        _repo.InsertSegment(new Segment { TsStartMs = 5000, TsEndMs = 6000, TextSrc = "Three", CreatedAt = 1 });
        _repo.InsertSegment(new Segment { TsStartMs = 1000, TsEndMs = 2000, TextSrc = "One", CreatedAt = 2 });
        _repo.InsertSegment(new Segment { TsStartMs = 3000, TsEndMs = 4000, TextSrc = "Two", CreatedAt = 3 });

        // Act
        var activeSegments = _repo.GetActiveSegments();

        // Assert
        Assert.Equal(3, activeSegments.Count);
        Assert.Equal("One", activeSegments[0].TextSrc);
        Assert.Equal("Two", activeSegments[1].TextSrc);
        Assert.Equal("Three", activeSegments[2].TextSrc);
    }
}
