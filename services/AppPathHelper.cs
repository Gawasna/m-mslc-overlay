using System;
using System.IO;

namespace m_mslc_overlay.services
{
    /// <summary>
    /// Utility for dynamically routing storage paths between Dev-mode and Production-mode (%LOCALAPPDATA%).
    /// </summary>
    public static class AppPathHelper
    {
        private static readonly Lazy<bool> _isDevMode = new Lazy<bool>(DetectDevMode);
        private static readonly Lazy<string> _devRepoRoot = new Lazy<string>(FindDevRepoRoot);
        private static readonly Lazy<string> _appDataDir = new Lazy<string>(ResolveAppDataDir);

        /// <summary>
        /// True if the application is running in development mode (plugins.manifest.json found in parent directory).
        /// </summary>
        public static bool IsDevMode => _isDevMode.Value;

        /// <summary>
        /// Root directory for application writable data.
        /// - Dev-mode: AppContext.BaseDirectory
        /// - Production: %LOCALAPPDATA%\m-mslc-overlay
        /// </summary>
        public static string AppDataDir => _appDataDir.Value;

        /// <summary>
        /// Returns absolute writable path for a given relative path, ensuring parent directory exists.
        /// </summary>
        public static string GetWritablePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            string appData = AppDataDir;
            string fullPath = Path.Combine(appData, relativePath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !string.Equals(dir, appData, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return fullPath;
        }

        private static string ResolveAppDataDir()
        {
            if (IsDevMode)
            {
                return AppContext.BaseDirectory;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(localAppData, "m-mslc-overlay");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }

        public static string GetConfigFilePath() => GetWritablePath("config.json");

        public static string GetLogsDirectory() => GetWritablePath("logs");

        public static string GetLockFilePath() => GetWritablePath("plugins.lock.json");

        public static string GetPluginsDirectory() => GetWritablePath("plugins");

        public static string GetExtractorDirectory()
        {
            if (IsDevMode)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string path = Path.Combine(localAppData, "m-mslc-overlay-dev", "extractor");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                return path;
            }
            return GetWritablePath("extractor");
        }

        /// <summary>
        /// Resolves path for plugins.manifest.json.
        /// </summary>
        public static string GetPluginManifestPath()
        {
            string baseManifest = Path.Combine(AppContext.BaseDirectory, "plugins.manifest.json");
            if (File.Exists(baseManifest)) return baseManifest;

            if (IsDevMode && !string.IsNullOrEmpty(_devRepoRoot.Value))
            {
                string devManifest = Path.Combine(_devRepoRoot.Value, "plugins.manifest.json");
                if (File.Exists(devManifest)) return devManifest;
            }

            return GetWritablePath("plugins.manifest.json");
        }

        private static bool DetectDevMode()
        {
            return !string.IsNullOrEmpty(FindDevRepoRoot());
        }

        private static string FindDevRepoRoot()
        {
            string? dir = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "plugins.manifest.json");
                if (File.Exists(candidate))
                {
                    return dir;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
            return string.Empty;
        }
    }
}
