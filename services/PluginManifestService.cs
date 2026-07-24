using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    // ---- Data Models ----

    public class AtomManifestEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        // "local:plugins/atom26" in dev-mode, or "https://..." for production releases
        [JsonPropertyName("source_url")]
        public string SourceUrl { get; set; } = string.Empty;

        // SHA256 hex string — empty string means skip verification (dev-mode local)
        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("install_dir")]
        public string InstallDir { get; set; } = string.Empty;

        [JsonPropertyName("entry_script")]
        public string EntryScript { get; set; } = string.Empty;

        [JsonPropertyName("min_python")]
        public string MinPython { get; set; } = string.Empty;

        [JsonPropertyName("changelog_url")]
        public string ChangelogUrl { get; set; } = string.Empty;
    }

    public class PluginsManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("generated_at")]
        public string GeneratedAt { get; set; } = string.Empty;

        [JsonPropertyName("atoms")]
        public AtomManifestEntry[] Atoms { get; set; } = Array.Empty<AtomManifestEntry>();
    }

    // ---- Service ----

    /// <summary>
    /// Manifest-driven plugin manager (Pattern B).
    /// Reads plugins.manifest.json, handles download/verify/extract for remote atoms,
    /// and local-copy fallback for dev-mode atoms (source_url starts with "local:").
    ///
    /// Flow:
    ///   1. LoadManifestAsync() — parse manifest
    ///   2. IsAtomInstalled() — check lock file
    ///   3. EnsureInstalledAsync() — download+verify+extract if needed
    ///   4. PluginInstallLockManager.RecordInstallation() — stamp lock file
    /// </summary>
    public static class PluginManifestService
    {
        private static readonly string ManifestPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "plugins.manifest.json");

        // Fallback: check repo root during development (app runs from bin/Debug)
        private static readonly string ManifestDevPath = FindManifestDevPath();

        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MSLC-Overlay/1.0 (Windows; PluginManifestService)");
            return client;
        }

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static string FindManifestDevPath()
        {
            // Crawl up from BaseDirectory to find plugins.manifest.json in repo root
            string? dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "plugins.manifest.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
            return string.Empty;
        }

        // ---- Public API ----

        /// <summary>
        /// Loads and parses plugins.manifest.json. Checks app base dir first, then
        /// crawls up for dev-mode repo root.
        /// </summary>
        public static async Task<PluginsManifest?> LoadManifestAsync()
        {
            string path = File.Exists(ManifestPath) ? ManifestPath : ManifestDevPath;
            if (string.IsNullOrEmpty(path))
            {
                LoggerService.Log("[PluginManifestService] plugins.manifest.json not found.");
                return null;
            }

            try
            {
                string json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<PluginsManifest>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[PluginManifestService] Failed to parse manifest: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the AtomManifestEntry for a given atomId, or null.
        /// </summary>
        public static async Task<AtomManifestEntry?> FindAtomAsync(string atomId)
        {
            var manifest = await LoadManifestAsync();
            if (manifest == null) return null;
            foreach (var atom in manifest.Atoms)
            {
                if (atom.Id == atomId) return atom;
            }
            return null;
        }

        /// <summary>
        /// Checks if atom is already installed at the correct version per lock file.
        /// </summary>
        public static bool IsAtomInstalled(string atomId, string expectedVersion)
        {
            return PluginInstallLockManager.IsInstalled(atomId, expectedVersion);
        }

        /// <summary>
        /// Ensures the atom's source files are present in install_dir.
        /// - If source_url starts with "local:", performs file copy from dev source dir.
        /// - If source_url starts with "https://", downloads ZIP, verifies SHA256, extracts.
        /// Returns true on success.
        /// </summary>
        public static async Task<bool> EnsureInstalledAsync(
            string atomId,
            Action<string> onLog,
            Action<double> onProgress)
        {
            var atom = await FindAtomAsync(atomId);
            if (atom == null)
            {
                onLog($"[PluginManifestService] Atom '{atomId}' not found in manifest.");
                return false;
            }

            // Resolve install dir (relative to BaseDir or repo root)
            string installDir = ResolveInstallDir(atom.InstallDir);

            // Check already installed at correct version
            if (PluginInstallLockManager.IsInstalled(atomId, atom.Version))
            {
                // Verify entry script still physically exists
                string entryPath = Path.Combine(installDir, atom.EntryScript);
                if (File.Exists(entryPath))
                {
                    onLog($"[PluginManifestService] {atom.Name} v{atom.Version} already installed. Skipping.");
                    return true;
                }
                // Lock file stale — reinstall
                onLog($"[PluginManifestService] Lock entry exists but {atom.EntryScript} is missing. Reinstalling...");
                PluginInstallLockManager.RemoveRecord(atomId);
            }

            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            bool success;

            if (atom.SourceUrl.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                // Dev-mode: copy from local source directory
                success = await LocalCopyAsync(atom, installDir, onLog, onProgress);
            }
            else if (atom.SourceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                  || atom.SourceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                // Production: download ZIP from remote URL
                success = await RemoteDownloadAsync(atom, installDir, onLog, onProgress);
            }
            else
            {
                onLog($"[PluginManifestService] Unknown source_url scheme: {atom.SourceUrl}");
                return false;
            }

            if (success)
            {
                PluginInstallLockManager.RecordInstallation(
                    atomId,
                    atom.Version,
                    installDir,
                    atom.SourceUrl,
                    sha256Verified: !string.IsNullOrEmpty(atom.Sha256));

                onLog($"[PluginManifestService] {atom.Name} v{atom.Version} installed successfully.");
            }

            return success;
        }

        /// <summary>
        /// Checks if the manifest has a newer version than what's recorded in the lock file.
        /// Returns true if an update is available.
        /// </summary>
        public static async Task<bool> IsAtomUpToDateAsync(string atomId)
        {
            var atom = await FindAtomAsync(atomId);
            if (atom == null) return true; // Can't determine — assume up to date

            var record = PluginInstallLockManager.GetRecord(atomId);
            if (record == null) return false; // Not installed at all

            return record.Version == atom.Version;
        }

        // ---- Private Helpers ----

        /// <summary>
        /// Resolves install_dir relative to app BaseDirectory or repo root.
        /// </summary>
        private static string ResolveInstallDir(string installDir)
        {
            if (Path.IsPathRooted(installDir)) return installDir;

            // Try BaseDirectory first (production)
            string fromBase = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, installDir));
            if (Directory.Exists(fromBase)) return fromBase;

            // Try repo root (dev mode)
            if (!string.IsNullOrEmpty(ManifestDevPath))
            {
                string repoRoot = Path.GetDirectoryName(ManifestDevPath)!;
                return Path.GetFullPath(Path.Combine(repoRoot, installDir));
            }

            return fromBase;
        }

        /// <summary>
        /// Dev-mode: copies source files from "local:relative/path" into installDir.
        /// </summary>
        private static async Task<bool> LocalCopyAsync(
            AtomManifestEntry atom,
            string installDir,
            Action<string> onLog,
            Action<double> onProgress)
        {
            // Strip "local:" prefix to get relative path
            string localRelPath = atom.SourceUrl.Substring("local:".Length).TrimStart('/', '\\');

            // Resolve from repo root (ManifestDevPath parent)
            string repoRoot = !string.IsNullOrEmpty(ManifestDevPath)
                ? Path.GetDirectoryName(ManifestDevPath)!
                : AppDomain.CurrentDomain.BaseDirectory;

            string sourceDir = Path.GetFullPath(Path.Combine(repoRoot, localRelPath));

            if (!Directory.Exists(sourceDir))
            {
                onLog($"[PluginManifestService] [DEV] Local source dir not found: {sourceDir}");
                return false;
            }

            onLog($"[PluginManifestService] [DEV] Copying from local source: {sourceDir}");

            // Same dir — nothing to copy
            if (string.Equals(sourceDir, installDir, StringComparison.OrdinalIgnoreCase))
            {
                onLog("[PluginManifestService] [DEV] Source and install dirs are identical. Skipping copy.");
                onProgress(100.0);
                return true;
            }

            try
            {
                onProgress(10.0);
                await Task.Run(() => CopyDirectoryRecursive(sourceDir, installDir, onLog));
                onProgress(100.0);
                onLog("[PluginManifestService] [DEV] Local copy completed.");
                return true;
            }
            catch (Exception ex)
            {
                onLog($"[PluginManifestService] [DEV] Copy failed: {ex.Message}");
                return false;
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir, Action<string> onLog)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destDir, fileName);
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(subDir);
                // Skip venv, models, cache dirs — these are installed separately
                if (dirName == "venv" || dirName == "models" || dirName == "__pycache__"
                    || dirName.StartsWith("."))
                {
                    continue;
                }
                CopyDirectoryRecursive(subDir, Path.Combine(destDir, dirName), onLog);
            }
        }

        /// <summary>
        /// Production: downloads ZIP from HTTPS URL, verifies SHA256, extracts to installDir.
        /// </summary>
        private static async Task<bool> RemoteDownloadAsync(
            AtomManifestEntry atom,
            string installDir,
            Action<string> onLog,
            Action<double> onProgress)
        {
            string tempZip = Path.Combine(Path.GetTempPath(), $"mslc_{atom.Id}_{atom.Version}.zip");

            try
            {
                // Step A: Download
                onLog($"[PluginManifestService] Downloading {atom.Name} v{atom.Version}...");
                onLog($"  Source: {atom.SourceUrl}");
                onProgress(5.0);

                using (var response = await _httpClient.GetAsync(atom.SourceUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        long downloaded = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            downloaded += bytesRead;

                            if (totalBytes.HasValue && totalBytes > 0)
                            {
                                double pct = 5.0 + (downloaded * 60.0 / totalBytes.Value);
                                onProgress(Math.Min(65.0, pct));
                            }
                        }
                    }
                }

                onLog($"[PluginManifestService] Download complete: {tempZip}");
                onProgress(65.0);

                // Step B: Verify SHA256 (skip if sha256 is empty — allows dev manifest without hash)
                if (!string.IsNullOrWhiteSpace(atom.Sha256))
                {
                    onLog("[PluginManifestService] Verifying SHA256 checksum...");
                    string actualHash = ComputeSha256(tempZip);

                    if (!string.Equals(actualHash, atom.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        onLog($"[PluginManifestService] SHA256 MISMATCH! Expected: {atom.Sha256}");
                        onLog($"[PluginManifestService] SHA256 MISMATCH! Actual:   {actualHash}");
                        onLog("[PluginManifestService] Aborting installation to prevent corrupt plugin.");
                        return false;
                    }
                    onLog("[PluginManifestService] SHA256 verified.");
                }
                else
                {
                    onLog("[PluginManifestService] Warning: sha256 is empty in manifest — skipping integrity check.");
                }

                onProgress(70.0);

                // Step C: Extract ZIP
                onLog($"[PluginManifestService] Extracting to: {installDir}");
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(tempZip);

                    // Detect common ZIP root folder prefix (e.g., "mslc-atom26-v1.0.0/")
                    string? zipRoot = DetectZipRootPrefix(archive);

                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entries

                        string entryRelPath = string.IsNullOrEmpty(zipRoot)
                            ? entry.FullName
                            : entry.FullName.Substring(zipRoot.Length);

                        // Skip __pycache__, .venv, models directories inside ZIP
                        if (entryRelPath.Contains("__pycache__/")
                            || entryRelPath.StartsWith(".venv/")
                            || entryRelPath.StartsWith("venv/")
                            || entryRelPath.StartsWith("models/"))
                        {
                            continue;
                        }

                        string destPath = Path.GetFullPath(Path.Combine(installDir, entryRelPath));

                        // Guard against ZipSlip attack
                        if (!destPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                });

                onProgress(100.0);
                onLog("[PluginManifestService] Extraction complete.");
                return true;
            }
            catch (Exception ex)
            {
                onLog($"[PluginManifestService] Download/extract failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); }
                    catch { /* best-effort cleanup */ }
                }
            }
        }

        private static string? DetectZipRootPrefix(ZipArchive archive)
        {
            // If all entries share a common root folder prefix, strip it
            string? prefix = null;
            foreach (var entry in archive.Entries)
            {
                int slash = entry.FullName.IndexOf('/');
                if (slash < 0) return null; // At least one file at root — no common prefix

                string candidate = entry.FullName.Substring(0, slash + 1);
                if (prefix == null)
                {
                    prefix = candidate;
                }
                else if (prefix != candidate)
                {
                    return null;
                }
            }
            return prefix;
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
