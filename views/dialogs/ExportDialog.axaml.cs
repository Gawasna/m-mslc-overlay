using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class ExportDialog : Window
    {
        private readonly Action<string>? _onExport;

        public ExportDialog() : this(null)
        {
        }

        public ExportDialog(Action<string>? onExport)
        {
            InitializeComponent();
            _onExport = onExport;
            
            // Set default values
            TargetPathInput.Text = AppDomain.CurrentDomain.BaseDirectory;
            FilenameInput.Text = $"export_{DateTime.Now:yyyyMMdd_HHmmss}";
            
            UpdateExportButtonState();
        }

        private async void BrowseBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var storage = this.StorageProvider;
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Target Export Directory",
                    AllowMultiple = false
                });

                if (folders != null && folders.Count > 0)
                {
                    TargetPathInput.Text = folders[0].Path.LocalPath;
                }
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[ExportDialog] Error selecting folder: {ex.Message}");
            }
        }

        private void OffsetPreset_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null && VideoTimeOffsetInput != null)
            {
                VideoTimeOffsetInput.Text = btn.Tag.ToString();
            }
        }

        private async void BrowseVideoBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var storage = this.StorageProvider;
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Chọn file video",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new("Video")
                        {
                            Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.webm", "*.avi", "*.m4v", "*.wmv", "*.ts" }
                        },
                        FilePickerFileTypes.All
                    }
                });

                if (files != null && files.Count > 0)
                {
                    string local = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                    VideoPathInput.Text = local;

                    // Suggest output filename from video stem if still default-ish
                    string stem = Path.GetFileNameWithoutExtension(local);
                    if (!string.IsNullOrWhiteSpace(stem)
                        && (string.IsNullOrWhiteSpace(FilenameInput.Text)
                            || FilenameInput.Text.StartsWith("export_", StringComparison.OrdinalIgnoreCase)))
                    {
                        FilenameInput.Text = stem + "_subs";
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[ExportDialog] Error selecting video: {ex.Message}");
            }

            UpdateExportButtonState();
        }

        private void OnCheckboxChanged(object? sender, RoutedEventArgs e)
        {
            UpdateExportButtonState();
        }

        private void TargetPathInput_TextChanged(object? sender, TextChangedEventArgs e)
        {
            UpdateExportButtonState();
        }

        private void UpdateExportButtonState()
        {
            if (ExportBtn == null || TargetPathInput == null)
                return;

            bool any =
                ExportSubtitlesCheck?.IsChecked == true
                || ExportAudioCheck?.IsChecked == true
                || ExportVideoSubCheck?.IsChecked == true;

            bool pathOk = !string.IsNullOrWhiteSpace(TargetPathInput.Text);
            bool videoOk = ExportVideoSubCheck?.IsChecked != true
                || !string.IsNullOrWhiteSpace(VideoPathInput?.Text);

            ExportBtn.IsEnabled = any && pathOk && videoOk;
        }

        private void CancelBtn_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void HelpBtn_Click(object? sender, RoutedEventArgs e)
        {
            _ = MessageDialog.ShowAsync(this, "Trợ giúp",
                "Xuất phụ đề (.srt, .ass, .vtt), âm thanh (.mp3, .wav, .flac) và/hoặc ghép phụ đề tách riêng vào video (bật/tắt trong player).\n\n"
                + "Ghép phụ đề vào video: lần đầu app sẽ tự tải công cụ xử lý video (~80–100 MB), không cần cài gì thêm.\n\n"
                + "Lệch phụ đề (giây): số âm = phụ đề sớm hơn (ví dụ -8 nếu transcript muộn hơn video ~8 giây); số dương = phụ đề muộn hơn.\n\n"
                + "Màu phụ đề: áp dụng khi xuất ASS hoặc ghép MKV. MP4 không giữ màu cố định.\n\n"
                + "Ngôn ngữ phụ đề khi ghép video lấy từ “Chế độ Ngôn ngữ & Nội dung” ở cột phụ đề.\n\n"
                + "Chọn thư mục lưu và (nếu ghép video) chọn file video để bật nút Xuất.");
        }

        private void ExportBtn_Click(object? sender, RoutedEventArgs e)
        {
            string targetPath = TargetPathInput.Text ?? "";
            string filename = FilenameInput.Text ?? "";

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                _ = MessageDialog.ShowAsync(this, "Thông báo", "Vui lòng chọn thư mục lưu trữ!");
                return;
            }

            if (string.IsNullOrWhiteSpace(filename))
            {
                _ = MessageDialog.ShowAsync(this, "Thông báo", "Vui lòng nhập tên tệp tin!");
                return;
            }

            bool enableVideo = ExportVideoSubCheck?.IsChecked == true;
            string videoPath = VideoPathInput?.Text?.Trim() ?? "";
            if (enableVideo && (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)))
            {
                _ = MessageDialog.ShowAsync(this, "Thông báo", "Vui lòng chọn file video hợp lệ để ghép phụ đề.");
                return;
            }

            string subtitleColor = (SubtitleColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?? "Trắng (White)";

            var subConfig = ExportSubtitlesCheck.IsChecked == true ? new SubtitleConfig
            {
                Format = (SubtitleFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? ".SRT",
                ContentMode = (ContentModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Song ngữ (EN + VI)",
                Encoding = (SubtitleEncodingCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UTF-8",
                IncludeStyles = IncludeStylesCheck.IsChecked == true,
                MergeOverlapping = MergeLinesCheck.IsChecked == true,
                ColorPreset = subtitleColor
            } : null;

            string audioFormat = "MP3";
            if (AudioFormatWav.IsChecked == true) audioFormat = "WAV";
            else if (AudioFormatFlac.IsChecked == true) audioFormat = "FLAC";

            string audioChannels = AudioChannelsMono.IsChecked == true ? "Mono" : "Stereo";
            string audioMode = AudioModeSegment.IsChecked == true ? "Segment" : "Merge";

            var audioConfig = ExportAudioCheck.IsChecked == true ? new AudioConfig
            {
                Format = audioFormat,
                Mode = audioMode,
                Bitrate = (AudioBitrateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "192 kbps",
                Channels = audioChannels,
                NormalizeVolume = NormalizeVolumeCheck.IsChecked == true
            } : null;

            string contentMode = (ContentModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?? "Chỉ Tiếng Việt (VI)";
            string container = VideoContainerMp4?.IsChecked == true ? "MP4" : "MKV";
            string videoColor = (VideoSubtitleColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?? subtitleColor;
            string muxSubFormat = container == "MP4" ? "SRT" : "ASS";

            double offsetSeconds = 0;
            string offsetText = VideoTimeOffsetInput?.Text?.Trim() ?? "0";
            if (!string.IsNullOrEmpty(offsetText)
                && !double.TryParse(offsetText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out offsetSeconds)
                && !double.TryParse(offsetText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out offsetSeconds))
            {
                _ = MessageDialog.ShowAsync(this, "Thông báo",
                    "Lệch phụ đề (giây) không hợp lệ. Nhập số, ví dụ: -8 hoặc 1.5");
                return;
            }

            long offsetMs = (long)Math.Round(offsetSeconds * 1000.0);

            var videoConfig = enableVideo ? new VideoSubtitleConfig
            {
                VideoPath = videoPath,
                Container = container,
                SubtitleFormat = muxSubFormat,
                ContentMode = contentMode,
                SetAsDefault = true,
                TimeOffsetMs = offsetMs,
                ColorPreset = videoColor
            } : null;

            var config = new ExportConfig
            {
                EnableSubtitle = ExportSubtitlesCheck.IsChecked == true,
                SubtitleConfig = subConfig,
                EnableAudio = ExportAudioCheck.IsChecked == true,
                AudioConfig = audioConfig,
                EnableVideoSubtitle = enableVideo,
                VideoSubtitleConfig = videoConfig,
                OutputPath = targetPath,
                FileNamePattern = filename,
                Overwrite = OverwriteCheck.IsChecked == true
            };

            try
            {
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                string jsonPayload = JsonSerializer.Serialize(config, options);

                LoggerService.Log($"[ExportDialog] Exporting with config payload:\n{jsonPayload}");
                
                _onExport?.Invoke(jsonPayload);

                Close(true);
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[ExportDialog] Serialization error: {ex.Message}");
                _ = MessageDialog.ShowAsync(this, "Lỗi", $"Lỗi tạo dữ liệu cấu hình: {ex.Message}");
            }
        }

        private class ExportConfig
        {
            [JsonPropertyName("enableSubtitle")]
            public bool EnableSubtitle { get; set; }
            
            [JsonPropertyName("subtitleConfig")]
            public SubtitleConfig? SubtitleConfig { get; set; }
            
            [JsonPropertyName("enableAudio")]
            public bool EnableAudio { get; set; }
            
            [JsonPropertyName("audioConfig")]
            public AudioConfig? AudioConfig { get; set; }

            [JsonPropertyName("enableVideoSubtitle")]
            public bool EnableVideoSubtitle { get; set; }

            [JsonPropertyName("videoSubtitleConfig")]
            public VideoSubtitleConfig? VideoSubtitleConfig { get; set; }
            
            [JsonPropertyName("outputPath")]
            public string OutputPath { get; set; } = "";
            
            [JsonPropertyName("fileNamePattern")]
            public string FileNamePattern { get; set; } = "";
            
            [JsonPropertyName("overwrite")]
            public bool Overwrite { get; set; }
        }

        private class SubtitleConfig
        {
            [JsonPropertyName("format")]
            public string Format { get; set; } = "";
            
            [JsonPropertyName("contentMode")]
            public string ContentMode { get; set; } = "";

            [JsonPropertyName("encoding")]
            public string Encoding { get; set; } = "";
            
            [JsonPropertyName("includeStyles")]
            public bool IncludeStyles { get; set; }
            
            [JsonPropertyName("mergeOverlapping")]
            public bool MergeOverlapping { get; set; }

            [JsonPropertyName("colorPreset")]
            public string ColorPreset { get; set; } = "Trắng (White)";
        }

        private class AudioConfig
        {
            [JsonPropertyName("format")]
            public string Format { get; set; } = "";
            
            [JsonPropertyName("mode")]
            public string Mode { get; set; } = "";

            [JsonPropertyName("bitrate")]
            public string Bitrate { get; set; } = "";
            
            [JsonPropertyName("channels")]
            public string Channels { get; set; } = "";
            
            [JsonPropertyName("normalizeVolume")]
            public bool NormalizeVolume { get; set; }
        }

        private class VideoSubtitleConfig
        {
            [JsonPropertyName("videoPath")]
            public string VideoPath { get; set; } = "";

            [JsonPropertyName("container")]
            public string Container { get; set; } = "MKV";

            [JsonPropertyName("subtitleFormat")]
            public string SubtitleFormat { get; set; } = "SRT";

            [JsonPropertyName("contentMode")]
            public string ContentMode { get; set; } = "Chỉ Tiếng Việt (VI)";

            [JsonPropertyName("setAsDefault")]
            public bool SetAsDefault { get; set; } = true;

            [JsonPropertyName("timeOffsetMs")]
            public long TimeOffsetMs { get; set; }

            [JsonPropertyName("colorPreset")]
            public string ColorPreset { get; set; } = "Trắng (White)";
        }
    }
}
