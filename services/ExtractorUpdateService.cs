using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; } = 0;
    }

    public static class ExtractorUpdateService
    {
        private const string RepoUrl = "https://api.github.com/repos/Gawasna/mslc-extractor/releases";
        
        public static event Action<string>? OnLogReceived;
        public static event Action<double>? OnProgressChanged;
        public static event Action<bool, string>? OnUpdateCompleted;

        private static CancellationTokenSource? _cts;

        private static void Log(string message)
        {
            OnLogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
            LoggerService.Log($"[ExtractorUpdate] {message}");
        }

        public static void Cancel()
        {
            _cts?.Cancel();
            Log("Đã gửi yêu cầu hủy cập nhật.");
        }

        public static async Task<GitHubRelease?> CheckForUpdateAsync()
        {
            try
            {
                using var client = new HttpClient();
                // GitHub API yêu cầu User-Agent
                client.DefaultRequestHeaders.Add("User-Agent", "mslc-overlay-updater");

                var response = await client.GetAsync($"{RepoUrl}/latest");
                if (!response.IsSuccessStatusCode)
                {
                    // Thử lấy danh sách nếu /latest bị lỗi
                    var listResponse = await client.GetAsync(RepoUrl);
                    if (!listResponse.IsSuccessStatusCode)
                    {
                        LoggerService.Log($"[ExtractorUpdate] Không thể kết nối với GitHub API. Code: {listResponse.StatusCode}");
                        return null;
                    }

                    var listJson = await listResponse.Content.ReadAsStringAsync();
                    var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(listJson);
                    if (releases != null && releases.Count > 0)
                    {
                        return releases[0];
                    }
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<GitHubRelease>(json);
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[ExtractorUpdate] Lỗi khi kiểm tra cập nhật: {ex.Message}");
                return null;
            }
        }

        public static async Task RunUpdateAsync(GitHubRelease release)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                Log($"Bắt đầu cập nhật lên phiên bản {release.TagName}...");
                OnProgressChanged?.Invoke(5);

                // Tìm file zip phù hợp (Windows x64)
                GitHubAsset? targetAsset = null;
                foreach (var asset in release.Assets)
                {
                    if (asset.Name.Contains("win-x64") && asset.Name.EndsWith(".zip"))
                    {
                        targetAsset = asset;
                        break;
                    }
                }

                if (targetAsset == null)
                {
                    throw new Exception("Không tìm thấy tệp nén phù hợp cho Windows x64 trong bản phát hành.");
                }

                string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp_update");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
                Directory.CreateDirectory(tempDir);

                string zipPath = Path.Combine(tempDir, targetAsset.Name);
                string extractDir = Path.Combine(tempDir, "extracted");

                Log($"Đang tải xuống tệp: {targetAsset.Name} ({targetAsset.Size / 1024} KB)...");
                
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "mslc-overlay-updater");
                    var progress = new Progress<double>(val =>
                    {
                        // Map tiến trình tải (5% -> 80%)
                        double mappedProgress = 5 + (val * 0.75);
                        OnProgressChanged?.Invoke(mappedProgress);
                    });

                    await DownloadFileWithProgressAsync(client, targetAsset.BrowserDownloadUrl, zipPath, progress, token);
                }

                token.ThrowIfCancellationRequested();
                Log("Tải xuống hoàn tất. Đang giải nén các tệp...");
                OnProgressChanged?.Invoke(85);

                ZipFile.ExtractToDirectory(zipPath, extractDir);

                token.ThrowIfCancellationRequested();
                Log("Giải nén hoàn tất. Đang kiểm tra mã băm bảo mật (SHA256)...");
                OnProgressChanged?.Invoke(90);

                string checksumFile = Path.Combine(extractDir, "SHA256SUMS.txt");
                if (!File.Exists(checksumFile))
                {
                    throw new Exception("Không tìm thấy tệp SHA256SUMS.txt để kiểm tra tính toàn vẹn.");
                }

                // Đọc và kiểm tra mã băm
                var expectedHashes = ParseChecksums(checksumFile);
                foreach (var kvp in expectedHashes)
                {
                    string fileName = kvp.Key;
                    string expectedHash = kvp.Value;
                    string filePath = Path.Combine(extractDir, fileName);

                    if (!File.Exists(filePath))
                    {
                        throw new Exception($"Thiếu tệp {fileName} được chỉ định trong SHA256SUMS.txt");
                    }

                    string actualHash = ComputeSHA256(filePath);
                    if (actualHash != expectedHash)
                    {
                        throw new Exception($"Lỗi kiểm tra tính toàn vẹn: Tệp {fileName} có mã băm không khớp.\nKỳ vọng: {expectedHash}\nThực tế: {actualHash}");
                    }
                    Log($"✓ Xác thực thành công tệp: {fileName}");
                }

                token.ThrowIfCancellationRequested();
                Log("Xác thực mã băm thành công. Đang ánh xạ và sao chép các tệp...");
                OnProgressChanged?.Invoke(95);

                string extractorDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extractor");
                if (!Directory.Exists(extractorDir))
                {
                    Directory.CreateDirectory(extractorDir);
                }

                // Copy các file được liệt kê trong checksums sang thư mục extractor chính thức
                foreach (var fileName in expectedHashes.Keys)
                {
                    string sourcePath = Path.Combine(extractDir, fileName);
                    string destPath = Path.Combine(extractorDir, fileName);

                    // Xử lý ghi đè (nếu file đang bị khóa bởi tiến trình khác, có thể sẽ bị lỗi, ta cần đóng tiến trình trước)
                    if (File.Exists(destPath))
                    {
                        try
                        {
                            File.Delete(destPath);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Không thể ghi đè tệp {fileName}. Có thể tệp đang được sử dụng bởi một tiến trình khác. Lỗi: {ex.Message}");
                        }
                    }

                    File.Copy(sourcePath, destPath, true);
                    Log($"Đã sao chép: {fileName} -> extractor/");
                }

                // Cập nhật cấu hình
                ConfigManager.Current.ExtractorTag = release.TagName;
                ConfigManager.Save();

                // Dọn dẹp thư mục tạm
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    Log($"Cảnh báo: Không thể dọn dẹp thư mục tạm: {ex.Message}");
                }

                OnProgressChanged?.Invoke(100);
                Log("Hoàn tất cập nhật Active Extractor!");
                OnUpdateCompleted?.Invoke(true, "Cập nhật thành công!");
            }
            catch (OperationCanceledException)
            {
                Log("Quá trình cập nhật đã bị người dùng hủy bỏ.");
                OnUpdateCompleted?.Invoke(false, "Đã hủy cập nhật.");
            }
            catch (Exception ex)
            {
                Log($"Lỗi: {ex.Message}");
                OnUpdateCompleted?.Invoke(false, ex.Message);
            }
        }

        private static async Task DownloadFileWithProgressAsync(HttpClient client, string url, string destinationPath, IProgress<double> progress, CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            var totalReadBytes = 0L;
            var readBytes = 0;

            while ((readBytes = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, readBytes, cancellationToken);
                totalReadBytes += readBytes;

                if (totalBytes != -1)
                {
                    var progressPercentage = (double)totalReadBytes / totalBytes * 100.0;
                    progress.Report(progressPercentage);
                }
            }
        }

        private static Dictionary<string, string> ParseChecksums(string checksumFilePath)
        {
            var dict = new Dictionary<string, string>();
            var lines = File.ReadAllLines(checksumFilePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Định dạng: <hash>  <filename>
                var parts = line.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string hash = parts[0].Trim().ToLowerInvariant();
                    string fileName = parts[1].Trim();
                    dict[fileName] = hash;
                }
            }
            return dict;
        }

        private static string ComputeSHA256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
