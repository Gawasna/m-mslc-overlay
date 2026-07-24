using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using MMslcOverlay.Core.Workspace.Models;

namespace MMslcOverlay.Core.Workspace.Repositories;

/// <summary>
/// Human Truth Repository - Đọc/ghi userdata.db (Event Sourcing)
/// </summary>
public class UserDataRepository
{
    private readonly string _connectionString;

    public UserDataRepository(string dbFilePath)
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
            CREATE TABLE IF NOT EXISTS patch_events (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type    TEXT NOT NULL,
                segment_ref   TEXT NOT NULL,
                field         TEXT NOT NULL,
                value_old     TEXT,
                value_new     TEXT NOT NULL,
                reverses_id   INTEGER,
                created_at    INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS annotations (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                scope         TEXT NOT NULL,
                segment_ref   TEXT,
                type          TEXT NOT NULL,
                content       TEXT,
                color         TEXT,
                created_at    INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ui_state (
                key           TEXT PRIMARY KEY,
                value         TEXT NOT NULL,
                updated_at    INTEGER NOT NULL
            );
            
            CREATE TABLE IF NOT EXISTS freeform_blocks (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                anchor_after    TEXT,
                content         TEXT NOT NULL,
                created_at      INTEGER NOT NULL,
                updated_at      INTEGER NOT NULL
            );
        ";
        command.ExecuteNonQuery();
        
        using var walCmd = connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode = WAL;";
        walCmd.ExecuteNonQuery();
    }

    public long InsertPatchEvent(PatchEvent evt)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO patch_events (
                event_type, segment_ref, field, value_old, value_new, reverses_id, created_at
            )
            VALUES (
                @event_type, @segment_ref, @field, @value_old, @value_new, @reverses_id, @created_at
            );
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@event_type", evt.EventType);
        command.Parameters.AddWithValue("@segment_ref", evt.SegmentRef);
        command.Parameters.AddWithValue("@field", evt.Field);
        command.Parameters.AddWithValue("@value_old", evt.ValueOld ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@value_new", evt.ValueNew);
        command.Parameters.AddWithValue("@reverses_id", evt.ReversesId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@created_at", evt.CreatedAt);

        var id = (long)command.ExecuteScalar()!;
        evt.Id = id;
        return id;
    }

    public List<PatchEvent> GetAllPatchEvents()
    {
        var events = new List<PatchEvent>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, event_type, segment_ref, field, value_old, value_new, reverses_id, created_at
            FROM patch_events
            ORDER BY created_at ASC, id ASC;
        ";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new PatchEvent
            {
                Id = reader.GetInt64(0),
                EventType = reader.GetString(1),
                SegmentRef = reader.GetString(2),
                Field = reader.GetString(3),
                ValueOld = reader.IsDBNull(4) ? null : reader.GetString(4),
                ValueNew = reader.GetString(5),
                ReversesId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                CreatedAt = reader.GetInt64(7)
            });
        }

        return events;
    }

    public void SaveUiState(string key, string value, long updatedAt)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO ui_state (key, value, updated_at)
            VALUES (@key, @value, @updated_at)
            ON CONFLICT(key) DO UPDATE SET 
                value = excluded.value, 
                updated_at = excluded.updated_at;
        ";

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@updated_at", updatedAt);
        
        command.ExecuteNonQuery();
    }

    public string? GetUiState(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ui_state WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return reader.GetString(0);
        }

        return null;
    }

    public long InsertAnnotation(Annotation annotation)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO annotations (
                scope, segment_ref, type, content, color, created_at
            )
            VALUES (
                @scope, @segment_ref, @type, @content, @color, @created_at
            );
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@scope", annotation.Scope);
        command.Parameters.AddWithValue("@segment_ref", annotation.SegmentRef ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@type", annotation.Type);
        command.Parameters.AddWithValue("@content", annotation.Content ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@color", annotation.Color ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@created_at", annotation.CreatedAt);

        var id = (long)command.ExecuteScalar()!;
        annotation.Id = id;
        return id;
    }

    public List<Annotation> GetAllAnnotations()
    {
        var list = new List<Annotation>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, scope, segment_ref, type, content, color, created_at
            FROM annotations
            ORDER BY created_at ASC;
        ";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Annotation
            {
                Id = reader.GetInt64(0),
                Scope = reader.GetString(1),
                SegmentRef = reader.IsDBNull(2) ? null : reader.GetString(2),
                Type = reader.GetString(3),
                Content = reader.IsDBNull(4) ? null : reader.GetString(4),
                Color = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = reader.GetInt64(6)
            });
        }
        return list;
    }

    public long InsertFreeformBlock(FreeformBlock block)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO freeform_blocks (
                anchor_after, content, created_at, updated_at
            )
            VALUES (
                @anchor_after, @content, @created_at, @updated_at
            );
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@anchor_after", block.AnchorAfter ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@content", block.Content);
        command.Parameters.AddWithValue("@created_at", block.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", block.UpdatedAt);

        var id = (long)command.ExecuteScalar()!;
        block.Id = id;
        return id;
    }

    public void UpdateFreeformBlock(FreeformBlock block)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE freeform_blocks
            SET content = @content,
                updated_at = @updated_at
            WHERE id = @id;
        ";

        command.Parameters.AddWithValue("@id", block.Id);
        command.Parameters.AddWithValue("@content", block.Content);
        command.Parameters.AddWithValue("@updated_at", block.UpdatedAt);

        command.ExecuteNonQuery();
    }

    public List<FreeformBlock> GetAllFreeformBlocks()
    {
        var list = new List<FreeformBlock>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, anchor_after, content, created_at, updated_at
            FROM freeform_blocks
            ORDER BY created_at ASC;
        ";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FreeformBlock
            {
                Id = reader.GetInt64(0),
                AnchorAfter = reader.IsDBNull(1) ? null : reader.GetString(1),
                Content = reader.GetString(2),
                CreatedAt = reader.GetInt64(3),
                UpdatedAt = reader.GetInt64(4)
            });
        }
        return list;
    }
}
