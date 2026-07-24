using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Repositories;

/// <summary>
/// Machine Truth Repository - Đọc/ghi base.db (active.db hoặc seg_NNN.db)
/// Luật bất biến: Chỉ INSERT. Không bao giờ UPDATE hoặc DELETE.
/// </summary>
public class BaseSegmentRepository
{
    private readonly string _connectionString;

    public BaseSegmentRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath};Mode=ReadWriteCreate";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS segments (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_start_ms   INTEGER NOT NULL,
                ts_end_ms     INTEGER NOT NULL,
                speaker_id    TEXT,
                text_src      TEXT NOT NULL,
                text_trs      TEXT,
                commit_type   TEXT NOT NULL,
                supersedes_id INTEGER,
                chunk_id      TEXT NOT NULL,
                created_at    INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ts ON segments(ts_start_ms);
        ";
        command.ExecuteNonQuery();
        
        // Enable WAL mode for better concurrency
        using var walCmd = connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode = WAL;";
        walCmd.ExecuteNonQuery();
    }

    public long InsertSegment(Segment segment)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO segments (
                ts_start_ms, ts_end_ms, speaker_id, text_src, text_trs, 
                commit_type, supersedes_id, chunk_id, created_at
            )
            VALUES (
                @ts_start_ms, @ts_end_ms, @speaker_id, @text_src, @text_trs, 
                @commit_type, @supersedes_id, @chunk_id, @created_at
            );
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@ts_start_ms", segment.TsStartMs);
        command.Parameters.AddWithValue("@ts_end_ms", segment.TsEndMs);
        command.Parameters.AddWithValue("@speaker_id", segment.SpeakerId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@text_src", segment.TextSrc);
        command.Parameters.AddWithValue("@text_trs", segment.TextTrs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@commit_type", segment.CommitType);
        command.Parameters.AddWithValue("@supersedes_id", segment.SupersedesId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@chunk_id", segment.ChunkId);
        command.Parameters.AddWithValue("@created_at", segment.CreatedAt);

        var id = (long)command.ExecuteScalar()!;
        segment.Id = id;
        return id;
    }

    /// <summary>
    /// Lấy tất cả các segment, chỉ lấy bản ghi cuối cùng của mỗi chuỗi (lọc supersedes_id)
    /// </summary>
    public List<Segment> GetActiveSegments()
    {
        var segments = new List<Segment>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        // Lấy tất cả, filter `WHERE supersedes_id IS NULL` theo rules
        command.CommandText = @"
            SELECT id, ts_start_ms, ts_end_ms, speaker_id, text_src, text_trs, 
                   commit_type, supersedes_id, chunk_id, created_at
            FROM segments
            WHERE supersedes_id IS NULL
            ORDER BY ts_start_ms ASC;
        ";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            segments.Add(new Segment
            {
                Id = reader.GetInt64(0),
                TsStartMs = reader.GetInt64(1),
                TsEndMs = reader.GetInt64(2),
                SpeakerId = reader.IsDBNull(3) ? null : reader.GetString(3),
                TextSrc = reader.GetString(4),
                TextTrs = reader.IsDBNull(5) ? null : reader.GetString(5),
                CommitType = reader.GetString(6),
                SupersedesId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                ChunkId = reader.GetString(8),
                CreatedAt = reader.GetInt64(9)
            });
        }

        return segments;
    }
}
