using System;
using System.IO;
using Microsoft.Data.Sqlite;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Storage;

public class ChunkManager
{
    private readonly WorkspaceStorage _storage;
    private SessionMeta _sessionMeta;

    public ChunkManager(WorkspaceStorage storage)
    {
        _storage = storage;
        _sessionMeta = storage.LoadOrCreateSessionMeta();
    }

    public void SealActiveChunk()
    {
        var activeDbPath = _storage.GetSegmentDbPath("active");
        var activeOffsetsPath = _storage.GetSegmentOffsetsPath("active");

        if (!File.Exists(activeDbPath))
        {
            return; // No active chunk to seal
        }

        // Flush WAL
        using (var connection = new SqliteConnection($"Data Source={activeDbPath};Mode=ReadWrite"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }

        // Tạo tên chunk mới, ví dụ: seg_001
        var nextIndex = _sessionMeta.SealedChunks.Count + 1;
        var newChunkId = $"seg_{nextIndex:D3}";
        var newDbPath = _storage.GetSegmentDbPath(newChunkId);
        var newOffsetsPath = _storage.GetSegmentOffsetsPath(newChunkId);

        // Rename DB
        File.Move(activeDbPath, newDbPath);

        // Move wal/shm if exists (should be checkpointed though, but safely rename or delete)
        var walPath = activeDbPath + "-wal";
        var shmPath = activeDbPath + "-shm";
        if (File.Exists(walPath)) File.Move(walPath, newDbPath + "-wal");
        if (File.Exists(shmPath)) File.Move(shmPath, newDbPath + "-shm");

        // Rename offsets
        if (File.Exists(activeOffsetsPath))
        {
            File.Move(activeOffsetsPath, newOffsetsPath);
        }

        // Cập nhật session meta
        _sessionMeta.SealedChunks.Add(newChunkId);
        _sessionMeta.LastUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _storage.SaveSessionMeta(_sessionMeta);
    }
}
