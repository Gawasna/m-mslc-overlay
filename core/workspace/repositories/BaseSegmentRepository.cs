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
                audio_session_id TEXT,
                audio_offset_ms INTEGER,
                speaker_id    TEXT,
                text_src      TEXT NOT NULL,
                text_trs      TEXT,
                commit_type   TEXT NOT NULL,
                supersedes_id INTEGER,
                chunk_id      TEXT NOT NULL,
                created_at    INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ts ON segments(ts_start_ms);
            CREATE INDEX IF NOT EXISTS idx_audio_session ON segments(audio_session_id);
        ";
        command.ExecuteNonQuery();
        
        // CRITICAL-TEXT-001: Auto-migration for acoustic metadata columns
        EnsureAcousticMetadataColumns(connection);
        
        // Enable WAL mode for better concurrency
        using var walCmd = connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode = WAL;";
        walCmd.ExecuteNonQuery();
    }
    
    /// <summary>
    /// CRITICAL-TEXT-001: Auto-migration to add acoustic metadata columns.
    /// Safe to run multiple times - checks if columns exist first.
    /// </summary>
    private void EnsureAcousticMetadataColumns(SqliteConnection connection)
    {
        // Get existing columns
        var existingColumns = new HashSet<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(segments);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(1)); // Column name at index 1
            }
        }
        
        // Add missing columns (SQLite doesn't support IF NOT EXISTS for ALTER TABLE)
        if (!existingColumns.Contains("acoustic_end_ms"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE segments ADD COLUMN acoustic_end_ms REAL;");
        }
        if (!existingColumns.Contains("utterance_offset"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE segments ADD COLUMN utterance_offset INTEGER;");
        }
        if (!existingColumns.Contains("audio_session_id"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE segments ADD COLUMN audio_session_id TEXT;");
        }
        if (!existingColumns.Contains("audio_offset_ms"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE segments ADD COLUMN audio_offset_ms INTEGER;");
        }
        if (!existingColumns.Contains("is_dangling"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE segments ADD COLUMN is_dangling INTEGER DEFAULT 0;");
        }
        if (!existingColumns.Contains("avg_speech_speed_ms"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE segments ADD COLUMN avg_speech_speed_ms INTEGER;");
        }
        if (!existingColumns.Contains("commit_reason"))
        {
            ExecuteNonQuery(connection, "ALTER TABLE segments ADD COLUMN commit_reason TEXT DEFAULT 'UNKNOWN';");
        }
    }
    
    private void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long InsertSegment(Segment segment)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO segments (
                ts_start_ms, ts_end_ms, audio_session_id, audio_offset_ms, speaker_id, text_src, text_trs, 
                commit_type, supersedes_id, chunk_id, created_at,
                acoustic_end_ms, utterance_offset, is_dangling, avg_speech_speed_ms, commit_reason
            )
            VALUES (
                @ts_start_ms, @ts_end_ms, @audio_session_id, @audio_offset_ms, @speaker_id, @text_src, @text_trs, 
                @commit_type, @supersedes_id, @chunk_id, @created_at,
                @acoustic_end_ms, @utterance_offset, @is_dangling, @avg_speech_speed_ms, @commit_reason
            );
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@ts_start_ms", segment.TsStartMs);
        command.Parameters.AddWithValue("@ts_end_ms", segment.TsEndMs);
        command.Parameters.AddWithValue("@audio_session_id", segment.AudioSessionId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@audio_offset_ms", segment.AudioOffsetMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@speaker_id", segment.SpeakerId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@text_src", segment.TextSrc);
        command.Parameters.AddWithValue("@text_trs", segment.TextTrs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@commit_type", segment.CommitType);
        command.Parameters.AddWithValue("@supersedes_id", segment.SupersedesId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@chunk_id", segment.ChunkId);
        command.Parameters.AddWithValue("@created_at", segment.CreatedAt);
        
        // CRITICAL-TEXT-001: Store acoustic metadata
        command.Parameters.AddWithValue("@acoustic_end_ms", 
            segment.AcousticEndMs.HasValue ? (object)segment.AcousticEndMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("@utterance_offset", 
            segment.UtteranceOffset.HasValue ? (object)(long)segment.UtteranceOffset.Value : DBNull.Value);
        command.Parameters.AddWithValue("@is_dangling", segment.IsDangling ? 1 : 0);
        command.Parameters.AddWithValue("@avg_speech_speed_ms", 
            segment.AvgSpeechSpeedMs.HasValue ? (object)segment.AvgSpeechSpeedMs.Value : DBNull.Value);
        command.Parameters.AddWithValue("@commit_reason", segment.CommitReason ?? "UNKNOWN");

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
        // Lấy tất cả, filter ra những record không bị thay thế bởi bất kỳ record nào khác
        command.CommandText = @"
            SELECT id, ts_start_ms, ts_end_ms, audio_session_id, audio_offset_ms, speaker_id, text_src, text_trs, 
                   commit_type, supersedes_id, chunk_id, created_at,
                   acoustic_end_ms, utterance_offset, is_dangling, avg_speech_speed_ms, commit_reason
            FROM segments s1
            WHERE NOT EXISTS (
                SELECT 1 FROM segments s2 WHERE s2.supersedes_id = s1.id
            )
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
                AudioSessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                AudioOffsetMs = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                SpeakerId = reader.IsDBNull(5) ? null : reader.GetString(5),
                TextSrc = reader.GetString(6),
                TextTrs = reader.IsDBNull(7) ? null : reader.GetString(7),
                CommitType = reader.GetString(8),
                SupersedesId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                ChunkId = reader.GetString(10),
                CreatedAt = reader.GetInt64(11),
                AcousticEndMs = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                UtteranceOffset = reader.IsDBNull(13) ? null : (ulong)reader.GetInt64(13),
                IsDangling = reader.IsDBNull(14) ? false : reader.GetInt32(14) == 1,
                AvgSpeechSpeedMs = reader.IsDBNull(15) ? null : (int?)reader.GetInt64(15),
                CommitReason = reader.IsDBNull(16) ? null : reader.GetString(16)
            });
        }

        return segments;
    }

    /// <summary>
    /// Cập nhật bản dịch máy (text_trs) cho segment theo ID
    /// </summary>
    public void UpdateSegmentTranslation(long id, string textTrs)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE segments 
            SET text_trs = @text_trs 
            WHERE id = @id;
        ";

        command.Parameters.AddWithValue("@text_trs", textTrs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@id", id);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Force checkpoint WAL to main database file.
    /// Call this before closing workspace to ensure all pending writes are persisted.
    /// </summary>
    public void FlushWal()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            // PRAGMA wal_checkpoint(TRUNCATE) forces all WAL frames to be written to the main DB
            // and then truncates the WAL file to zero bytes
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();

            System.Diagnostics.Debug.WriteLine($"[BaseSegmentRepository] WAL checkpoint completed for {_connectionString}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BaseSegmentRepository] WAL checkpoint failed: {ex.Message}");
            // Don't throw - this is a best-effort cleanup
        }
    }
}
