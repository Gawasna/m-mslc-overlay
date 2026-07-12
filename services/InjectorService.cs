using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    public class InjectorService
    {
        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string? ResolveHostPath()
        {
            string hostPath = Path.Combine(AppContext.BaseDirectory, "extractor", "Host.exe");

            if (!File.Exists(hostPath))
            {
                hostPath = Path.Combine(AppContext.BaseDirectory, "Host.exe");
            }

            if (!File.Exists(hostPath))
            {
                string rootDir = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.FullName
                    ?? AppContext.BaseDirectory;
                hostPath = Path.Combine(rootDir, "Host.exe");
            }

            return File.Exists(hostPath) ? hostPath : null;
        }

        public async Task<(bool Success, string ErrorMessage)> InjectAsync(uint pid)
        {
            try
            {
                string? hostPath = ResolveHostPath();
                if (hostPath == null)
                {
                    return (false, "Host.exe not found. Run: .\\mslc extractor");
                }

                string hostDir = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory;
                string agentPath = Path.Combine(hostDir, "Agent.dll");

                if (!File.Exists(agentPath))
                {
                    return (false, $"Agent.dll not found: {agentPath}. Run: .\\mslc extractor");
                }

                var startInfo = new ProcessStartInfo {
                    FileName = hostPath,
                    Arguments = $"--pid {pid} --inject-only",
                    WorkingDirectory = hostDir,
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                if (!IsRunningAsAdmin())
                {
                    startInfo.Verb = "runas";
                }

                using var process = new Process { StartInfo = startInfo };

                if (!process.Start())
                {
                    return (false, "Failed to start Host.exe.");
                }

                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    return (true, string.Empty);
                }

                return (false,
                    $"Host.exe failed (exit code {process.ExitCode}). " +
                    "Common causes: admin permission denied, antivirus blocked injection, or LiveCaptions restarted.");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return (false, "Administrator permission was denied (you clicked No).");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InjectorService] Injection error: {ex.Message}");
                return (false, $"Injection error: {ex.Message}");
            }
        }
    }
}