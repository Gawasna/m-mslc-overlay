using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace m_mslc_overlay.core
{
    /// <summary>
    /// Utility class for interacting with the Microsoft Live Captions process and settings.
    /// </summary>
    public static class LiveCaptionUtils
    {
        private const string ProcessName = "LiveCaptions";
        private const string SettingsUri = "ms-settings:privacy-livecaptions";

        /// <summary>
        /// Checks whether the Microsoft Live Captions process is currently running.
        /// </summary>
        /// <returns>True if running, otherwise false.</returns>
        public static bool IsLiveCaptionRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName(ProcessName);
                if (processes.Length > 0)
                {
                    using (var proc = processes[0])
                    {
                        return !proc.HasExited;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaptionUtils] Error checking process status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the Process ID of the running Microsoft Live Captions instance.
        /// </summary>
        /// <returns>The PID if running, otherwise 0.</returns>
        public static uint GetLiveCaptionProcessId()
        {
            try
            {
                var processes = Process.GetProcessesByName(ProcessName);
                if (processes.Length > 0)
                {
                    using (var proc = processes[0])
                    {
                        return (uint)proc.Id;
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaptionUtils] Error retrieving process ID: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Launches the Windows Settings page for Live Captions.
        /// </summary>
        /// <returns>True if settings page opened successfully, otherwise false.</returns>
        public static bool LaunchLiveCaptionSettings()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = SettingsUri,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaptionUtils] Error launching Settings: {ex.Message}");
                return false;
            }
        }
    }
}
