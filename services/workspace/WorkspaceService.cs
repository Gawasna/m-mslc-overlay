using System;
using System.Collections.Generic;
using System.IO;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Repositories;
using MMslcOverlay.Core.Workspace.Storage;

namespace MMslcOverlay.Services.Workspace;

public class WorkspaceService : IDisposable
{
    public WorkspaceStorage Storage { get; private set; }
    public ChunkManager? ChunkManager { get; private set; }
    public UserDataRepository? UserDataRepo { get; private set; }
    public SegmentRepository? SegmentRepo { get; private set; }
    public BaseSegmentRepository? ActiveSegmentRepo { get; private set; }
    
    private readonly List<BaseSegmentRepository> _baseRepos = new();
    
    // Services
    public SegmentIngestionService? IngestionService { get; private set; }
    public AudioRecorderService? AudioService { get; private set; }

    public WorkspaceService(string workspaceRoot)
    {
        Storage = new WorkspaceStorage(workspaceRoot);
    }

    public void OpenOrCreate()
    {
        Storage.Initialize();
        
        var sessionMeta = Storage.LoadOrCreateSessionMeta();
        ChunkManager = new ChunkManager(Storage);
        UserDataRepo = new UserDataRepository(Storage.UserDataDbPath);
        
        // Load all sealed chunks
        foreach (var chunkId in sessionMeta.SealedChunks)
        {
            var dbPath = Storage.GetSegmentDbPath(chunkId);
            if (File.Exists(dbPath))
            {
                _baseRepos.Add(new BaseSegmentRepository(dbPath));
            }
        }
        
        // Load active chunk
        var activeDbPath = Storage.GetSegmentDbPath(sessionMeta.ActiveChunkId);
        ActiveSegmentRepo = new BaseSegmentRepository(activeDbPath);
        _baseRepos.Add(ActiveSegmentRepo);
        
        SegmentRepo = new SegmentRepository(_baseRepos, UserDataRepo);
        
        // Initialize active Audio Offset Index
        var activeOffsetsPath = Storage.GetSegmentOffsetsPath(sessionMeta.ActiveChunkId);
        var activeAudioOffsetIndex = new AudioOffsetIndex(activeOffsetsPath);
        
        // Start sub-services
        IngestionService = new SegmentIngestionService(ActiveSegmentRepo, sessionMeta.ActiveChunkId);
        
        var audioFilePath = Path.Combine(Storage.MslcDir, "segments", $"{sessionMeta.ActiveChunkId}.audio.wav");
        AudioService = new AudioRecorderService(audioFilePath, activeAudioOffsetIndex);
    }
    
    public void Dispose()
    {
        AudioService?.Dispose();
    }
}
