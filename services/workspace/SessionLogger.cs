using System;
using System.IO;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Ghi nhận log của session vào trực tiếp tệp session.log bên trong thư mục .mslc của phiên làm việc.
/// </summary>
public static class SessionLogger
{
    private static readonly object _lock = new object();
    public static string? CurrentMslcDir { get; set; }

    public static void Log(string message)
    {
        string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(message);
        System.Diagnostics.Debug.WriteLine(message);

        try
        {
            if (!string.IsNullOrEmpty(CurrentMslcDir) && Directory.Exists(CurrentMslcDir))
            {
                lock (_lock)
                {
                    string logPath = Path.Combine(CurrentMslcDir, "session.log");
                    File.AppendAllText(logPath, timestamped + Environment.NewLine);
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi ghi log (ví dụ khi bị khóa tệp) để không làm gãy luồng chính
        }
    }
}
