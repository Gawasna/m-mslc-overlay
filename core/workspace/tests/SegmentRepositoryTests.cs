using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;

namespace MMslcOverlay.Core.Workspace.Tests;

public class SegmentRepositoryTests : IDisposable
{
    private readonly string _baseDbPath1;
    private readonly string _baseDbPath2;
    private readonly string _userDbPath;
    
    private readonly BaseSegmentRepository _baseRepo1;
    private readonly BaseSegmentRepository _baseRepo2;
    private readonly UserDataRepository _userDataRepo;
    private readonly SegmentRepository _segmentRepo;

    public SegmentRepositoryTests()
    {
        _baseDbPath1 = Path.GetTempFileName();
        _baseDbPath2 = Path.GetTempFileName();
        _userDbPath = Path.GetTempFileName();

        _baseRepo1 = new BaseSegmentRepository(_baseDbPath1);
        _baseRepo2 = new BaseSegmentRepository(_baseDbPath2);
        _userDataRepo = new UserDataRepository(_userDbPath);

        _segmentRepo = new SegmentRepository(
            new List<BaseSegmentRepository> { _baseRepo1, _baseRepo2 }, 
            _userDataRepo
        );
    }

    public void Dispose()
    {
        CleanupFile(_baseDbPath1);
        CleanupFile(_baseDbPath2);
        CleanupFile(_userDbPath);
    }

    private void CleanupFile(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
                if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
                if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public void GetMergedSegments_MergesMultipleChunksAndAppliesPatches()
    {
        // Arrange
        // Chunk 1 (seg_001)
        var id1 = _baseRepo1.InsertSegment(new Segment { 
            TsStartMs = 1000, TsEndMs = 2000, TextSrc = "Chunk 1 Segment 1", ChunkId = "seg_001" 
        });
        
        // Chunk 2 (active)
        var id2 = _baseRepo2.InsertSegment(new Segment { 
            TsStartMs = 3000, TsEndMs = 4000, TextSrc = "Chunk 2 Segment 1", ChunkId = "active" 
        });
        
        // Add patches in user data
        _userDataRepo.InsertPatchEvent(new PatchEvent {
            EventType = "PATCH",
            SegmentRef = $"seg_001:{id1}",
            Field = "text_src",
            ValueNew = "Chunk 1 Segment 1 (Patched)",
            CreatedAt = 1000
        });

        _userDataRepo.InsertPatchEvent(new PatchEvent {
            EventType = "PATCH",
            SegmentRef = $"active:{id2}",
            Field = "speaker_id",
            ValueNew = "SPK_TEST",
            CreatedAt = 2000
        });

        // Act
        var merged = _segmentRepo.GetMergedSegments();

        // Assert
        Assert.Equal(2, merged.Count);
        
        // Check seg_001
        Assert.Equal($"seg_001:{id1}", merged[0].SegmentRef);
        Assert.Equal("Chunk 1 Segment 1 (Patched)", merged[0].TextSrc); // Patched
        Assert.Null(merged[0].SpeakerId); // Not patched

        // Check active
        Assert.Equal($"active:{id2}", merged[1].SegmentRef);
        Assert.Equal("Chunk 2 Segment 1", merged[1].TextSrc); // Not patched
        Assert.Equal("SPK_TEST", merged[1].SpeakerId); // Patched
    }
}
