using System;
using System.IO;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    public enum PluginInstallState
    {
        Unknown,
        NotInManifest,
        NotInstalled,
        Installed,
        UpdateAvailable,
        Broken // lock says installed but entry script missing
    }

    public sealed class PluginStatus
    {
        public string AtomId { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string ManifestVersion { get; init; } = "";
        public string? InstalledVersion { get; init; }
        public string InstallDir { get; init; } = "";
        public string EntryScript { get; init; } = "";
        public PluginInstallState InstallState { get; init; } = PluginInstallState.Unknown;
        public bool EntryScriptPresent { get; init; }
        public bool EnvReady { get; init; }
        public string? PythonExe { get; init; }
        public string Summary { get; init; } = "";
    }

    /// <summary>
    /// Facade for plugin install status, install, and uninstall (atom26 / atom32).
    /// Runtime start/stop stays in Offline server / diarizer managers.
    /// </summary>
    public static class PluginManagerService
    {
        public static async Task<PluginStatus> GetStatusAsync(string atomId)
        {
            var atom = await PluginManifestService.FindAtomAsync(atomId);
            if (atom == null)
            {
                return new PluginStatus
                {
                    AtomId = atomId,
                    Name = atomId,
                    InstallState = PluginInstallState.NotInManifest,
                    Summary = "Không có trong plugins.manifest.json"
                };
            }

            string installDir = PluginManifestService.ResolveInstallDir(atom.InstallDir);
            bool scriptOk = PluginManifestService.IsEntryScriptPresent(atom);
            bool envOk = PluginPythonEnvService.IsEnvReady(installDir, atomId);
            string? pythonExe = envOk ? PluginPythonEnvService.GetVenvPythonPath(installDir, atomId) : null;
            var record = PluginInstallLockManager.GetRecord(atomId);

            PluginInstallState state;
            string summary;

            if (record == null && !scriptOk)
            {
                state = PluginInstallState.NotInstalled;
                summary = "Chưa cài package";
            }
            else if (scriptOk && !envOk)
            {
                state = PluginInstallState.Broken;
                summary = "Có files, thiếu venv/deps — bấm Cài lại để setup Python";
            }
            else if (record != null && !scriptOk)
            {
                state = PluginInstallState.Broken;
                summary = $"Hỏng (lock v{record.Version}, thiếu {atom.EntryScript})";
            }
            else if (record == null && scriptOk && envOk)
            {
                state = PluginInstallState.Installed;
                summary = $"Sẵn sàng (files + venv) · v{atom.Version}";
            }
            else if (record != null && record.Version != atom.Version)
            {
                state = PluginInstallState.UpdateAvailable;
                summary = envOk
                    ? $"Đã cài v{record.Version} (env OK) · có bản mới v{atom.Version}"
                    : $"Đã cài v{record.Version} · thiếu env · có bản mới v{atom.Version}";
            }
            else
            {
                state = PluginInstallState.Installed;
                summary = envOk
                    ? $"Sẵn sàng v{record!.Version} (package + venv)"
                    : $"v{record!.Version} · thiếu venv";
            }

            return new PluginStatus
            {
                AtomId = atom.Id,
                Name = atom.Name,
                Description = atom.Description,
                ManifestVersion = atom.Version,
                InstalledVersion = record?.Version,
                InstallDir = installDir,
                EntryScript = atom.EntryScript,
                InstallState = state,
                EntryScriptPresent = scriptOk,
                EnvReady = envOk,
                PythonExe = pythonExe,
                Summary = summary
            };
        }

        public static Task<bool> InstallAsync(
            string atomId,
            Action<string> onLog,
            Action<double> onProgress)
            => PluginManifestService.EnsureInstalledAsync(atomId, onLog, onProgress);

        public static async Task<bool> UninstallAsync(string atomId, Action<string>? onLog = null)
        {
            // Stop runtimes that own these atoms
            if (atomId == "atom26")
            {
                try { OfflineTranslationServerManager.StopServer(); }
                catch (Exception ex) { onLog?.Invoke($"Stop offline server: {ex.Message}"); }
            }
            else if (atomId == "atom32")
            {
                // Global stop if any manager instance is running is handled by app session;
                // best-effort: no singleton stop API beyond process exit on uninstall.
                onLog?.Invoke("[PluginManager] Ensure diarizer is stopped before deleting atom32 files.");
            }

            return await PluginManifestService.UninstallAsync(atomId, onLog);
        }

        public static bool CanStart(PluginStatus status)
            => status.EntryScriptPresent && status.EnvReady;

        /// <summary>Python for launching atom entry script, or null if env missing.</summary>
        public static async Task<string?> ResolvePythonExeAsync(string atomId)
        {
            var atom = await PluginManifestService.FindAtomAsync(atomId);
            if (atom == null) return null;
            string installDir = PluginManifestService.ResolveInstallDir(atom.InstallDir);
            string py = PluginPythonEnvService.GetVenvPythonPath(installDir, atomId);
            return File.Exists(py) ? py : null;
        }
    }
}
