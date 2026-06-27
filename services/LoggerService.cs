using System;
using System.IO;
using System.Linq;

namespace m_mslc_overlay.services
{
    public class LoggerService
    {
        private static string? _logPath;
        private static readonly object _fileLock = new object();

        public static void Initialize()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string logsDir = Path.Combine(baseDir, "logs");
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }

                _logPath = Path.Combine(logsDir, "mslc_ui_debug.log");
                RotateLogs(_logPath, logsDir);

                // Create new active log file with header
                File.WriteAllText(_logPath, $"[UI] === New Session Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoggerService] Init error: {ex.Message}");
            }
        }

        private static void RotateLogs(string activeLogPath, string logsDir)
        {
            try
            {
                // 1. If old active log exists, archive it using its last modification time
                if (File.Exists(activeLogPath))
                {
                    DateTime lastWrite = File.GetLastWriteTime(activeLogPath);
                    string timestamp = lastWrite.ToString("yyyyMMdd_HHmmss");
                    string archivePath = Path.Combine(logsDir, $"mslc_ui_debug_{timestamp}.log");

                    int counter = 1;
                    while (File.Exists(archivePath))
                    {
                        archivePath = Path.Combine(logsDir, $"mslc_ui_debug_{timestamp}_{counter}.log");
                        counter++;
                    }

                    File.Move(activeLogPath, archivePath);
                }

                // 2. Keep only the 4 most recent archive logs (plus the active one to be created = 5 total logs)
                var archiveFiles = Directory.GetFiles(logsDir, "mslc_ui_debug_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderBy(fi => fi.Name) // Sorting by filename containing timestamp sorts chronologically
                    .ToList();

                if (archiveFiles.Count > 4)
                {
                    int toDelete = archiveFiles.Count - 4;
                    for (int i = 0; i < toDelete; i++)
                    {
                        archiveFiles[i].Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoggerService] Rotate error: {ex.Message}");
            }
        }

        public static void Log(string message)
        {
            if (string.IsNullOrEmpty(_logPath)) return;

            lock (_fileLock)
            {
                try
                {
                    File.AppendAllText(_logPath, message);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LoggerService] Write error: {ex.Message}");
                }
            }
        }
    }
}
