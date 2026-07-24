using System;
using System.IO;
using Microsoft.Data.Sqlite;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Storage;

public enum SealTrigger
{
    SessionEnd,     // App tắt, user nhấn Stop
    PauseContinue,  // User dừng rồi tiếp tục sau khoảng lặng dài
    NaturalSilence, // Khoảng lặng dài tự nhiên
    CapacityReached // active.db quá lớn
}

public class ChunkManager
{
    private readonly WorkspaceStorage _storage;
    private SessionMeta _sessionMeta;

    public ChunkManager(WorkspaceStorage storage)
    {
        _storage = storage;
        _sessionMeta = storage.LoadOrCreateSessionMeta();
    }

    /// <summary>
    /// Kiểm tra ngưỡng kép (Dual Threshold) dựa trên spec 7.1
    /// </summary>
    public bool ShouldSealActiveChunk(SealTrigger trigger, TimeSpan silenceDuration)
    {
        var activeDbPath = _storage.GetSegmentDbPath("active");
        if (!File.Exists(activeDbPath)) return false;

        var fileInfo = new FileInfo(activeDbPath);
        long fileSizeMb = fileInfo.Length / (1024 * 1024);

        return trigger switch
        {
            SealTrigger.SessionEnd => true, // Luôn seal bất kể kích thước
            SealTrigger.PauseContinue => silenceDuration.TotalMinutes > 30 && fileSizeMb > 50,
            SealTrigger.NaturalSilence => silenceDuration.TotalMinutes > 15 && fileSizeMb > 30,
            SealTrigger.CapacityReached => fileSizeMb >= 200, // Vượt 200MB bất kể trigger khác
            _ => false
        };
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
