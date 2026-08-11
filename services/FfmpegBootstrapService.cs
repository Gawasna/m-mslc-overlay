using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m_mslc_overlay.services;

namespace MMslcOverlay.Services;

/// <summary>Ensures a local ffmpeg binary is available (download once if missing).</summary>
public static class FfmpegBootstrapService
{
    private const string FallbackDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private const string GitHubLatestApi = "https://api.github.com/repos/GyanD/codexffmpeg/releases/latest";
    private const string UserAgent = "m-mslc-overlay-ffmpeg-bootstrap";

    public record EnsureResult(bool Success, string? FfmpegPath, string? ErrorMessage, bool DidDownload);

    public static string GetInstallDirectory()
        => AppPathHelper.GetWritablePath(Path.Combine("tools", "ffmpeg"));

    public static string GetInstalledExePath()
        => Path.Combine(GetInstallDirectory(), "ffmpeg.exe");

    public static bool IsReady()
        => !string.IsNullOrEmpty(ResolveExistingPath());

    public static string? ResolveExistingPath()
    {
        string? env = Environment.GetEnvironmentVariable("MSLC_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        string managed = GetInstalledExePath();
        if (File.Exists(managed))
            return managed;

        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "tools", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string path = line.Trim();
                    if (File.Exists(path))
                        return path;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static async Task<EnsureResult> EnsureReadyAsync(
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        string? existing = ResolveExistingPath();
        if (existing != null)
        {
            progress?.Report(100);
            status?.Report("Công cụ xử lý video đã sẵn sàng.");
            return new EnsureResult(true, existing, null, DidDownload: false);
        }

        try
        {
            status?.Report("Đang tìm bản tải nhanh...");
            progress?.Report(1);

            string installDir = GetInstallDirectory();
            Directory.CreateDirectory(installDir);

            string tempRoot = AppPathHelper.GetWritablePath(Path.Combine("temp_ffmpeg"));
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { /* best effort */ }
            }
            Directory.CreateDirectory(tempRoot);

            string zipPath = Path.Combine(tempRoot, "ffmpeg-essentials.zip");

            using var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 8,
                EnableMultipleHttp2Connections = true
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            string downloadUrl = await ResolveFastDownloadUrlAsync(client, status, ct).ConfigureAwait(false)
                                 ?? FallbackDownloadUrl;

            status?.Report("Đang tải công cụ xử lý video (chỉ lần đầu)...");
            progress?.Report(3);

            var dlProgress = new Progress<(double pct, string detail)>(t =>
            {
                progress?.Report(3 + t.pct * 0.82);
                if (!string.IsNullOrEmpty(t.detail))
                    status?.Report(t.detail);
            });

            await DownloadFileWithProgressAsync(client, downloadUrl, zipPath, dlProgress, ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            status?.Report("Đang cài đặt (chỉ lấy file cần thiết)...");
            progress?.Report(90);

            string destFfmpeg = GetInstalledExePath();
            string destProbe = Path.Combine(installDir, "ffprobe.exe");

            bool gotFfmpeg = ExtractNamedEntry(zipPath, "ffmpeg.exe", destFfmpeg);
            if (!gotFfmpeg)
            {
                return new EnsureResult(false, null,
                    "Tải xong nhưng không tìm thấy thành phần cần thiết trong gói cài đặt.", DidDownload: true);
            }

            try { ExtractNamedEntry(zipPath, "ffprobe.exe", destProbe); } catch { /* optional */ }

            try { Directory.Delete(tempRoot, true); } catch { /* ignore */ }

            if (!File.Exists(destFfmpeg))
            {
                return new EnsureResult(false, null, "Cài đặt công cụ xử lý video thất bại.", DidDownload: true);
            }

            progress?.Report(100);
            status?.Report("Đã sẵn sàng.");
            LoggerService.Log($"[FfmpegBootstrap] Installed to {destFfmpeg}");
            return new EnsureResult(true, destFfmpeg, null, DidDownload: true);
        }
        catch (OperationCanceledException)
        {
            return new EnsureResult(false, null, "Đã hủy tải công cụ xử lý video.", DidDownload: false);
        }
        catch (Exception ex)
        {
            LoggerService.Log($"[FfmpegBootstrap] Failed: {ex.Message}");
            return new EnsureResult(false, null,
                $"Không tải được công cụ xử lý video. Kiểm tra mạng rồi thử lại.\nChi tiết: {ex.Message}",
                DidDownload: false);
        }
    }

    private static async Task<string?> ResolveFastDownloadUrlAsync(
        HttpClient client, IProgress<string>? status, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GitHubLatestApi);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            string? essentials = null;
            string? anyZip = null;
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                anyZip ??= url;
                if (name.Contains("essentials", StringComparison.OrdinalIgnoreCase))
                {
                    essentials = url;
                    break;
                }
            }

            string? chosen = essentials ?? anyZip;
            if (chosen != null)
            {
                status?.Report("Đã chọn nguồn tải nhanh (GitHub).");
                LoggerService.Log($"[FfmpegBootstrap] Download URL: {chosen}");
            }
            return chosen;
        }
        catch (Exception ex)
        {
            LoggerService.Log($"[FfmpegBootstrap] GitHub resolve failed, using fallback: {ex.Message}");
            return null;
        }
    }

    private static bool ExtractNamedEntry(string zipPath, string fileName, string destPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return false;

        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(destPath))
            File.Delete(destPath);

        entry.ExtractToFile(destPath, overwrite: true);
        return File.Exists(destPath);
    }

    private static async Task DownloadFileWithProgressAsync(
        HttpClient client,
        string url,
        string destinationPath,
        IProgress<(double pct, string detail)> progress,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 1024];
        long totalRead = 0;
        var sw = Stopwatch.StartNew();
        long lastReportBytes = 0;
        var lastReportTime = sw.Elapsed;
        int read;

        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;

            var now = sw.Elapsed;
            if ((now - lastReportTime).TotalMilliseconds >= 250 || totalRead == totalBytes)
            {
                double pct = totalBytes > 0
                    ? totalRead * 100.0 / totalBytes
                    : Math.Min(95, totalRead / (1024.0 * 1024.0));

                double seconds = Math.Max(0.001, (now - lastReportTime).TotalSeconds);
                double speedBps = (totalRead - lastReportBytes) / seconds;
                string speedStr = FormatSpeed(speedBps);
                string sizeStr = totalBytes > 0
                    ? $"{FormatMb(totalRead)} / {FormatMb(totalBytes)}"
                    : FormatMb(totalRead);

                progress.Report((pct, $"Đang tải... {sizeStr}  ·  {speedStr}"));
                lastReportBytes = totalRead;
                lastReportTime = now;
            }
        }

        // Flush
        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress.Report((100, $"Tải xong ({FormatMb(totalRead)})."));
    }

    private static string FormatMb(long bytes)
        => $"{bytes / (1024.0 * 1024.0):0.0} MB";

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):0.0} MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }
}
