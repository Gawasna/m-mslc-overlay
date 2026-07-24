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
}
