using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace m_mslc_overlay.services
{
    public class AtomInstallRecord
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("installed_at")]
        public string InstalledAt { get; set; } = string.Empty;

        [JsonPropertyName("install_dir")]
        public string InstallDir { get; set; } = string.Empty;

        [JsonPropertyName("sha256_verified")]
        public bool Sha256Verified { get; set; } = false;

        [JsonPropertyName("source_url")]
        public string SourceUrl { get; set; } = string.Empty;
    }

    public class PluginLockFile
    {
        [JsonPropertyName("installed")]
        public Dictionary<string, AtomInstallRecord> Installed { get; set; } = new();
    }

    /// <summary>
    /// Manages plugins.lock.json — records which atoms are installed, at what version,
    /// and verifies SHA256 integrity. This file is NOT committed to git.
    /// </summary>
    public static class PluginInstallLockManager
    {
        private static readonly string LockFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "plugins.lock.json");

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static PluginLockFile Load()
        {
            try
            {
                if (File.Exists(LockFilePath))
                {
                    string json = File.ReadAllText(LockFilePath);
                    var lockFile = JsonSerializer.Deserialize<PluginLockFile>(json);
                    if (lockFile != null)
                    {
                        return lockFile;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[PluginInstallLockManager] Failed to read lock file: {ex.Message}");
            }
            return new PluginLockFile();
        }

        public static void Save(PluginLockFile lockFile)
        {
            try
            {
                string json = JsonSerializer.Serialize(lockFile, _jsonOptions);
                File.WriteAllText(LockFilePath, json);
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[PluginInstallLockManager] Failed to write lock file: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true if the atom is installed at exactly the expected version.
        /// </summary>
        public static bool IsInstalled(string atomId, string expectedVersion)
        {
            var lockFile = Load();
            if (lockFile.Installed.TryGetValue(atomId, out var record))
            {
                return record.Version == expectedVersion;
            }
            return false;
        }

        /// <summary>
        /// Returns the install record for an atom, or null if not installed.
        /// </summary>
        public static AtomInstallRecord? GetRecord(string atomId)
        {
            var lockFile = Load();
            lockFile.Installed.TryGetValue(atomId, out var record);
            return record;
        }

        /// <summary>
        /// Records a successful installation into the lock file.
        /// </summary>
        public static void RecordInstallation(string atomId, string version, string installDir, string sourceUrl, bool sha256Verified)
        {
            var lockFile = Load();
            lockFile.Installed[atomId] = new AtomInstallRecord
            {
                Version = version,
                InstalledAt = DateTime.UtcNow.ToString("O"),
                InstallDir = installDir,
                Sha256Verified = sha256Verified,
                SourceUrl = sourceUrl
            };
            Save(lockFile);
            LoggerService.Log($"[PluginInstallLockManager] Recorded installation: {atomId} v{version} at {installDir}");
        }

        /// <summary>
        /// Removes an atom record from the lock file (e.g., after uninstall).
        /// </summary>
        public static void RemoveRecord(string atomId)
        {
            var lockFile = Load();
            if (lockFile.Installed.Remove(atomId))
            {
                Save(lockFile);
                LoggerService.Log($"[PluginInstallLockManager] Removed lock record for: {atomId}");
            }
        }
    }
}
