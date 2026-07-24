using System.IO;
using System.Text.Json;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Storage;

public class WorkspaceStorage
{
    private readonly string _workspaceRoot;
    
    public string WorkspaceName => Path.GetFileName(_workspaceRoot);
    public string MslcDir => Path.Combine(_workspaceRoot, ".mslc");
    public string SegmentsDir => Path.Combine(MslcDir, "segments");
    public string BackupsDir => Path.Combine(MslcDir, "backups");
    
    public string UserDataDbPath => Path.Combine(MslcDir, "userdata.db");
    public string SessionMetaPath => Path.Combine(MslcDir, "session_meta.json");
    
    // Exports
    public string ExportsDir => Path.Combine(_workspaceRoot, "exports");
    
    public WorkspaceStorage(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
    }
    
    public void Initialize()
    {
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(MslcDir);
        Directory.CreateDirectory(SegmentsDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(ExportsDir);
        
        // Hide .mslc directory on Windows
        var mslcDirInfo = new DirectoryInfo(MslcDir);
        if (!mslcDirInfo.Attributes.HasFlag(FileAttributes.Hidden))
        {
            mslcDirInfo.Attributes |= FileAttributes.Hidden;
        }
    }
    
    public SessionMeta LoadOrCreateSessionMeta()
    {
        if (File.Exists(SessionMetaPath))
        {
            var json = File.ReadAllText(SessionMetaPath);
            return JsonSerializer.Deserialize<SessionMeta>(json) ?? CreateNewMeta();
        }
        
        return CreateNewMeta();
    }
    
    public void SaveSessionMeta(SessionMeta meta)
    {
        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SessionMetaPath, json);
    }
    
    private SessionMeta CreateNewMeta()
    {
        var meta = new SessionMeta 
        { 
            SessionId = System.Guid.NewGuid().ToString(),
            CreatedAt = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        SaveSessionMeta(meta);
        return meta;
    }
    
    public string GetSegmentDbPath(string chunkId)
    {
        return Path.Combine(SegmentsDir, $"{chunkId}.db");
    }
    
    public string GetSegmentOffsetsPath(string chunkId)
    {
        return Path.Combine(SegmentsDir, $"{chunkId}.offsets.bin");
    }
}
