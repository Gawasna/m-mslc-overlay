using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MMslcOverlay.Services.Workspace;

public record VideoSubtitleMuxRequest(
    string VideoPath,
    string SubtitlePath,
    string OutputPath,
    string Container,
    string SubtitleCodecHint,
    string LanguageCode,
    string TrackTitle,
    bool SetAsDefault,
    bool Overwrite
);

public record VideoSubtitleMuxResult(bool Success, string OutputPath, string? ErrorMessage);

/// <summary>Mux a soft subtitle track into video via ffmpeg (stream copy).</summary>
public static class VideoSubtitleMuxService
{
    public static string? ResolveFfmpegPath()
        => MMslcOverlay.Services.FfmpegBootstrapService.ResolveExistingPath();

    public static async Task<VideoSubtitleMuxResult> MuxAsync(
        VideoSubtitleMuxRequest req,
        CancellationToken ct = default,
        string? ffmpegPath = null)
    {
        if (string.IsNullOrWhiteSpace(req.VideoPath) || !File.Exists(req.VideoPath))
            return new VideoSubtitleMuxResult(false, req.OutputPath, $"Không tìm thấy file video: {req.VideoPath}");

        if (string.IsNullOrWhiteSpace(req.SubtitlePath) || !File.Exists(req.SubtitlePath))
            return new VideoSubtitleMuxResult(false, req.OutputPath, $"Không tìm thấy file phụ đề: {req.SubtitlePath}");

        string? ffmpeg = !string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath)
            ? ffmpegPath
            : ResolveFfmpegPath();
        if (ffmpeg == null)
        {
            return new VideoSubtitleMuxResult(false, req.OutputPath,
                "Chưa có công cụ xử lý video. Hãy cho phép app tải tự động khi xuất, hoặc thử lại khi có mạng.");
        }

        string container = (req.Container ?? "MKV").Trim().ToUpperInvariant();
        if (container is not ("MKV" or "MP4"))
            container = "MKV";

        string outPath = req.OutputPath;
        string? outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        if (File.Exists(outPath) && !req.Overwrite)
            return new VideoSubtitleMuxResult(false, outPath, $"File đã tồn tại (bỏ qua ghi đè): {Path.GetFileName(outPath)}");

        bool useAss = container == "MKV"
            && req.SubtitleCodecHint.Equals("ASS", StringComparison.OrdinalIgnoreCase);

        string subCodec = container == "MP4"
            ? "mov_text"
            : (useAss ? "ass" : "srt");

        string lang = string.IsNullOrWhiteSpace(req.LanguageCode) ? "und" : req.LanguageCode.Trim().ToLowerInvariant();
        string title = string.IsNullOrWhiteSpace(req.TrackTitle) ? "Subtitles" : req.TrackTitle;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(req.VideoPath);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(req.SubtitlePath);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-c:s");
        psi.ArgumentList.Add(subCodec);
        psi.ArgumentList.Add("-metadata:s:s:0");
        psi.ArgumentList.Add($"language={lang}");
        psi.ArgumentList.Add("-metadata:s:s:0");
        psi.ArgumentList.Add($"title={title}");
        if (req.SetAsDefault)
        {
            psi.ArgumentList.Add("-disposition:s:0");
            psi.ArgumentList.Add("default");
        }
        if (container == "MP4")
        {
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
        }
        psi.ArgumentList.Add(outPath);

        var stderr = new StringBuilder();
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stderr.AppendLine(e.Data);
            };
            process.OutputDataReceived += (_, _) => { };

            if (!process.Start())
                return new VideoSubtitleMuxResult(false, outPath, "Không khởi động được ffmpeg.");

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string detail = Truncate(stderr.ToString(), 800);
                return new VideoSubtitleMuxResult(false, outPath,
                    $"ffmpeg thất bại (mã {process.ExitCode}). {detail}");
            }

            if (!File.Exists(outPath))
                return new VideoSubtitleMuxResult(false, outPath, "ffmpeg chạy xong nhưng không thấy file đầu ra.");

            return new VideoSubtitleMuxResult(true, outPath, null);
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* ignore */ }
            return new VideoSubtitleMuxResult(false, outPath, "Đã hủy ghép phụ đề.");
        }
        catch (Exception ex)
        {
            return new VideoSubtitleMuxResult(false, outPath, $"Lỗi khi chạy ffmpeg: {ex.Message}");
        }
    }

    public static string LanguageCodeFromContentMode(string? contentMode)
    {
        if (string.IsNullOrWhiteSpace(contentMode))
            return "und";

        bool bilingual = contentMode.Contains("EN + VI", StringComparison.OrdinalIgnoreCase)
            || contentMode.Contains("Song ngữ", StringComparison.OrdinalIgnoreCase);
        if (bilingual)
            return "und";

        if (contentMode.Contains("Vietnamese", StringComparison.OrdinalIgnoreCase)
            || contentMode.Contains("Tiếng Việt", StringComparison.OrdinalIgnoreCase)
            || (contentMode.Contains("VI", StringComparison.Ordinal) && !contentMode.Contains("EN", StringComparison.Ordinal)))
            return "vie";

        if (contentMode.Contains("English", StringComparison.OrdinalIgnoreCase)
            || contentMode.Contains("Tiếng Anh", StringComparison.OrdinalIgnoreCase)
            || contentMode.Contains("EN", StringComparison.Ordinal))
            return "eng";

        return "und";
    }

    public static string TrackTitleFromContentMode(string? contentMode)
    {
        if (string.IsNullOrWhiteSpace(contentMode))
            return "Subtitles";
        if (contentMode.Contains("Song ngữ", StringComparison.OrdinalIgnoreCase)
            || contentMode.Contains("EN + VI", StringComparison.OrdinalIgnoreCase))
            return "EN+VI";
        if (contentMode.Contains("VI", StringComparison.Ordinal)
            || contentMode.Contains("Việt", StringComparison.OrdinalIgnoreCase))
            return "Tiếng Việt";
        if (contentMode.Contains("EN", StringComparison.Ordinal)
            || contentMode.Contains("English", StringComparison.OrdinalIgnoreCase)
            || contentMode.Contains("Anh", StringComparison.OrdinalIgnoreCase))
            return "English";
        return "Subtitles";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s;
        return s.Substring(s.Length - max, max);
    }
}
