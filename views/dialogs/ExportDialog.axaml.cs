using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
                var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
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
            if (ExportBtn != null && ExportSubtitlesCheck != null && ExportAudioCheck != null && TargetPathInput != null)
            {
                ExportBtn.IsEnabled = (ExportSubtitlesCheck.IsChecked == true || ExportAudioCheck.IsChecked == true)
                    && !string.IsNullOrWhiteSpace(TargetPathInput.Text);
            }
        }

        private void CancelBtn_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void HelpBtn_Click(object? sender, RoutedEventArgs e)
        {
            _ = MessageDialog.ShowAsync(this, "Trợ giúp", "Hộp thoại cho phép cấu hình xuất dữ liệu Phụ đề (.srt, .ass, .vtt) và Âm thanh (.mp3, .wav, .flac) đồng bộ với cuộc hội thoại.\n\nVui lòng chọn thư mục lưu trữ hợp lệ để nút Xuất file (Export) được kích hoạt.");
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

            var subConfig = ExportSubtitlesCheck.IsChecked == true ? new SubtitleConfig
            {
                Format = (SubtitleFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? ".SRT",
                Encoding = (SubtitleEncodingCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UTF-8",
                IncludeStyles = IncludeStylesCheck.IsChecked == true,
                MergeOverlapping = MergeLinesCheck.IsChecked == true
            } : null;

            string audioFormat = "MP3";
            if (AudioFormatWav.IsChecked == true) audioFormat = "WAV";
            else if (AudioFormatFlac.IsChecked == true) audioFormat = "FLAC";

            string audioChannels = AudioChannelsMono.IsChecked == true ? "Mono" : "Stereo";

            var audioConfig = ExportAudioCheck.IsChecked == true ? new AudioConfig
            {
                Format = audioFormat,
                Bitrate = (AudioBitrateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "192 kbps",
                Channels = audioChannels,
                NormalizeVolume = NormalizeVolumeCheck.IsChecked == true
            } : null;

            var config = new ExportConfig
            {
                EnableSubtitle = ExportSubtitlesCheck.IsChecked == true,
                SubtitleConfig = subConfig,
                EnableAudio = ExportAudioCheck.IsChecked == true,
                AudioConfig = audioConfig,
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
            
            [JsonPropertyName("encoding")]
            public string Encoding { get; set; } = "";
            
            [JsonPropertyName("includeStyles")]
            public bool IncludeStyles { get; set; }
            
            [JsonPropertyName("mergeOverlapping")]
            public bool MergeOverlapping { get; set; }
        }

        private class AudioConfig
        {
            [JsonPropertyName("format")]
            public string Format { get; set; } = "";
            
            [JsonPropertyName("bitrate")]
            public string Bitrate { get; set; } = "";
            
            [JsonPropertyName("channels")]
            public string Channels { get; set; } = "";
            
            [JsonPropertyName("normalizeVolume")]
            public bool NormalizeVolume { get; set; }
        }
    }
}
