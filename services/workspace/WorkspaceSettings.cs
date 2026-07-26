using System;
using System.IO;
using System.Text.Json;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Persist workspace preferences qua sessions.
/// File: %AppData%\MMslcOverlay\settings.json
/// </summary>
public class WorkspaceSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MMslcOverlay", "settings.json");

    public string LastWorkspacePath { get; set; } = string.Empty;

    public static WorkspaceSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new WorkspaceSettings();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<WorkspaceSettings>(json) ?? new WorkspaceSettings();
        }
        catch
        {
            return new WorkspaceSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
    }

    /// <summary>
    /// Trả về last path nếu có, ngược lại tạo default path.
    /// </summary>
    public string ResolveWorkspacePath()
    {
        if (!string.IsNullOrEmpty(LastWorkspacePath)) return LastWorkspacePath;
        LastWorkspacePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MMslcOverlay", "default-workspace");
        Save();
        return LastWorkspacePath;
    }
}
