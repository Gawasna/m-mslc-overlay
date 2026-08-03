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
    public StreamingPcmRecorder? AudioRecorder { get; private set; }  // ✅ NEW

    public WorkspaceService(string workspaceRoot)
    {
        Storage = new WorkspaceStorage(workspaceRoot);
    }

    public void OpenOrCreate()
    {
        try
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
                else
                {
                    Console.WriteLine($"[Warning] Sealed chunk {chunkId} not found at {dbPath}. Skipping.");
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
            
            // ✅ PHASE 3: Run session recovery before creating new recorder
            string audioDir = Path.Combine(Storage.MslcDir, "audio");
            var recoveryService = new SessionRecoveryService(audioDir);
            var recoveredSessions = recoveryService.RecoverAllSessions();
            
            if (recoveredSessions.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[WorkspaceService] 🔧 Recovered {recoveredSessions.Count} incomplete audio sessions:");
                foreach (var recoveredId in recoveredSessions)
                {
                    System.Diagnostics.Debug.WriteLine($"  - {recoveredId}");
                }
            }
            
            // ✅ NEW: Initialize StreamingPcmRecorder
            string newSessionId = $"session_{DateTime.Now:yyyyMMdd_HHmmss}";
            AudioRecorder = new StreamingPcmRecorder(audioDir, newSessionId);
            System.Diagnostics.Debug.WriteLine($"[WorkspaceService] Audio recorder initialized: {newSessionId}");
            
            // Start sub-services with audio integration
            IngestionService = new SegmentIngestionService(
                ActiveSegmentRepo, 
                sessionMeta.ActiveChunkId,
                AudioRecorder,          // ✅ Pass recorder
                activeAudioOffsetIndex  // ✅ Pass offset index
            );
            
            var audioFilePath = Path.Combine(Storage.MslcDir, "segments", $"{sessionMeta.ActiveChunkId}.audio.wav");
            AudioService = new AudioRecorderService(audioFilePath, activeAudioOffsetIndex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to open workspace: {ex.Message}");
            throw new InvalidOperationException($"Không thể mở workspace: {ex.Message}", ex);
        }
    }
    
    public void Dispose()
    {
        AudioRecorder?.Dispose();  // ✅ Dispose recorder first (stops recording)
        AudioService?.Dispose();
        
        // ✅ FIX: Force WAL checkpoint before closing workspace
        // This ensures all pending writes in WAL are flushed to main DB
        if (ActiveSegmentRepo != null)
        {
            ActiveSegmentRepo.FlushWal();
        }
        
        foreach (var repo in _baseRepos)
        {
            repo.FlushWal();
        }
    }
}
