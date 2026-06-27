using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class PreferencesDialog : Window
    {
        public PreferencesDialog()
        {
            InitializeComponent();
            ConfigManager.Load();
            LoadSettings();

            OfflineTranslationServerManager.OnStateChanged += OnServerStateChanged;
            
            // Đăng ký các sự kiện cài đặt
            OfflineTranslationInstaller.OnLogReceived += OnInstallerLogReceived;
            OfflineTranslationInstaller.OnProgressChanged += OnInstallerProgressChanged;
            OfflineTranslationInstaller.OnInstallationCompleted += OnInstallerCompleted;

            this.Closed += (s, e) => {
                OfflineTranslationServerManager.OnStateChanged -= OnServerStateChanged;
                OfflineTranslationInstaller.OnLogReceived -= OnInstallerLogReceived;
                OfflineTranslationInstaller.OnProgressChanged -= OnInstallerProgressChanged;
                OfflineTranslationInstaller.OnInstallationCompleted -= OnInstallerCompleted;
            };
        }

        private void OnServerStateChanged(OfflineServerState state)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                UpdateServerStateUI(state);
            });
        }

        private void LoadSettings()
        {
            var cfg = ConfigManager.Current;
            StartupCheck.IsChecked = cfg.RunAtStartup;
            TrayIconCheck.IsChecked = cfg.StartMinimizedToTray;
            CheckUpdatesCheck.IsChecked = cfg.CheckForUpdates;
            
            LanguageCombo.SelectedIndex = cfg.Language == "vi-VN" ? 0 : 1;
            
            TranslationEngineCombo.SelectedIndex = cfg.TranslationEngine switch {
                "DeepL API" => 0,
                "Cloud AI (Ollama/Gemini)" => 1,
                "Offline CTranslate2" => 2,
                _ => 1
            };
            DeepLApiKeyBox.Text = cfg.DeepLApiKey;
            OfflineTranslateUrlBox.Text = cfg.OfflineTranslateUrl;
            OfflineServerDirBox.Text = cfg.OfflineServerDir;
            
            AiModelCombo.SelectedIndex = cfg.AiModel switch {
                "Gemini 1.5 Flash" => 1,
                "Claude 3 Haiku" => 2,
                _ => 0
            };
            
            ApiKeyBox.Text = cfg.ApiKey;
            SystemPromptBox.Text = cfg.SystemPrompt;
            PipeNameBox.Text = cfg.PipeName;
            VerboseLogCheck.IsChecked = cfg.VerboseLogging;
            EnableHotkeysCheck.IsChecked = cfg.EnableGlobalHotkeys;

            UpdateServerStateUI(OfflineTranslationServerManager.State);
        }

        private void SaveSettings()
        {
            var cfg = ConfigManager.Current;
            string oldEngine = cfg.TranslationEngine;

            cfg.RunAtStartup = StartupCheck.IsChecked ?? false;
            cfg.StartMinimizedToTray = TrayIconCheck.IsChecked ?? true;
            cfg.CheckForUpdates = CheckUpdatesCheck.IsChecked ?? true;
            
            cfg.Language = LanguageCombo.SelectedIndex == 0 ? "vi-VN" : "en-US";
            
            cfg.TranslationEngine = TranslationEngineCombo.SelectedIndex switch {
                0 => "DeepL API",
                1 => "Cloud AI (Ollama/Gemini)",
                2 => "Offline CTranslate2",
                _ => "Cloud AI (Ollama/Gemini)"
            };
            cfg.DeepLApiKey = DeepLApiKeyBox.Text ?? "";
            cfg.OfflineTranslateUrl = OfflineTranslateUrlBox.Text ?? "http://127.0.0.1:11435";
            cfg.OfflineServerDir = OfflineServerDirBox.Text ?? "plugins/atom26";
            
            cfg.AiModel = AiModelCombo.SelectedIndex switch {
                1 => "Gemini 1.5 Flash",
                2 => "Claude 3 Haiku",
                _ => "Gemini 1.5 Pro"
            };
            
            cfg.ApiKey = ApiKeyBox.Text ?? "";
            cfg.SystemPrompt = SystemPromptBox.Text ?? "";
            cfg.PipeName = PipeNameBox.Text ?? "MSLCCaptionPipe";
            cfg.VerboseLogging = VerboseLogCheck.IsChecked ?? false;
            cfg.EnableGlobalHotkeys = EnableHotkeysCheck.IsChecked ?? true;
            
            ConfigManager.Save();

            // Quản lý vòng đời Offline Server khi cấu hình Engine thay đổi
            if (oldEngine != cfg.TranslationEngine)
            {
                if (cfg.TranslationEngine == "Offline CTranslate2")
                {
                    LoggerService.Log("[PreferencesDialog] Translation engine switched to Offline CTranslate2. Starting offline server...");
                    // Parse port from URL if custom, else use default 11435
                    if (Uri.TryCreate(cfg.OfflineTranslateUrl, UriKind.Absolute, out var uri))
                    {
                        OfflineTranslationServerManager.ServerPort = uri.Port;
                    }
                    _ = OfflineTranslationServerManager.StartServerAsync();
                }
                else if (oldEngine == "Offline CTranslate2")
                {
                    LoggerService.Log("[PreferencesDialog] Translation engine switched away from Offline CTranslate2. Stopping offline server...");
                    OfflineTranslationServerManager.StopServer();
                }
            }

            if (this.Owner is MainWindow mainWin)
            {
                mainWin.UpdateHotkeyRegistration();
            }
        }

        private async void TestOfflineConnectionBtn_Click(object? sender, RoutedEventArgs e)
        {
            OfflineStatusText.Text = LanguageManager.GetString("Pref_LocalAI_Testing") ?? "Đang kiểm tra kết nối...";
            OfflineStatusText.Foreground = Avalonia.Media.Brushes.Gray;
            
            string url = OfflineTranslateUrlBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "http://127.0.0.1:11435";
            }

            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync($"{url.TrimEnd('/')}/status");
                response.EnsureSuccessStatusCode();

                string responseStr = await response.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(responseStr);
                var root = doc.RootElement;
                
                string status = root.GetProperty("status").GetString() ?? "";
                string device = root.GetProperty("device").GetString() ?? "";
                string modelType = root.GetProperty("model_type").GetString() ?? "";
                string modelPath = root.GetProperty("model_path").GetString() ?? "";
                bool hasCuda = root.GetProperty("has_cuda").GetBoolean();

                string statusMsg = LanguageManager.GetString("Pref_LocalAI_ConnSuccess") ?? "Kết nối thành công!";
                string deviceMsg = device.ToUpper();
                
                OfflineStatusText.Text = $"{statusMsg}\nDevice: {deviceMsg} (CUDA: {hasCuda})\nModel Type: {modelType.ToUpper()}\nPath: {modelPath}";
                OfflineStatusText.Foreground = Avalonia.Media.Brushes.Green;
            }
            catch (Exception ex)
            {
                string failMsg = LanguageManager.GetString("Pref_LocalAI_ConnFailed") ?? "Kết nối thất bại!";
                OfflineStatusText.Text = $"{failMsg} Error: {ex.Message}";
                OfflineStatusText.Foreground = Avalonia.Media.Brushes.Red;
            }
        }

        private void TabSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (TabSelector == null) return;
            
            // Hide all tabs
            if (TabGeneral != null) TabGeneral.IsVisible = false;
            if (TabTranslation != null) TabTranslation.IsVisible = false;
            if (TabAppearance != null) TabAppearance.IsVisible = false;
            if (TabNetwork != null) TabNetwork.IsVisible = false;
            if (TabAdvanced != null) TabAdvanced.IsVisible = false;
            if (TabHotkeys != null) TabHotkeys.IsVisible = false;

            // Show selected tab
            switch (TabSelector.SelectedIndex)
            {
                case 0:
                    if (TabGeneral != null) TabGeneral.IsVisible = true;
                    break;
                case 1:
                    if (TabTranslation != null) TabTranslation.IsVisible = true;
                    break;
                case 2:
                    if (TabAppearance != null) TabAppearance.IsVisible = true;
                    break;
                case 3:
                    if (TabNetwork != null) TabNetwork.IsVisible = true;
                    break;
                case 4:
                    if (TabAdvanced != null) TabAdvanced.IsVisible = true;
                    break;
                case 5:
                    if (TabHotkeys != null) TabHotkeys.IsVisible = true;
                    break;
            }
        }

        private void ResetBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Simple reset to defaults
            ConfigManager.Current = new AppConfig();
            LoadSettings();
        }

        private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        {
            SaveSettings();
            Close();
        }

        private void UpdateServerStateUI(OfflineServerState state)
        {
            if (OfflineServerStateText == null) return;

            switch (state)
            {
                case OfflineServerState.Stopped:
                    OfflineServerStateText.Text = "Đã dừng";
                    OfflineServerStateText.Foreground = Avalonia.Media.Brushes.Gray;
                    if (StartOfflineServerBtn != null) StartOfflineServerBtn.IsEnabled = true;
                    if (StopOfflineServerBtn != null) StopOfflineServerBtn.IsEnabled = false;
                    break;
                case OfflineServerState.Starting:
                    OfflineServerStateText.Text = "Đang khởi động...";
                    OfflineServerStateText.Foreground = Avalonia.Media.Brushes.Orange;
                    if (StartOfflineServerBtn != null) StartOfflineServerBtn.IsEnabled = false;
                    if (StopOfflineServerBtn != null) StopOfflineServerBtn.IsEnabled = true;
                    break;
                case OfflineServerState.Ready:
                    OfflineServerStateText.Text = "Sẵn sàng (Đang chạy)";
                    OfflineServerStateText.Foreground = Avalonia.Media.Brushes.Green;
                    if (StartOfflineServerBtn != null) StartOfflineServerBtn.IsEnabled = false;
                    if (StopOfflineServerBtn != null) StopOfflineServerBtn.IsEnabled = true;
                    break;
                case OfflineServerState.ModelMissing:
                    OfflineServerStateText.Text = "Thiếu mô hình (Model Missing)";
                    OfflineServerStateText.Foreground = Avalonia.Media.Brushes.Red;
                    if (StartOfflineServerBtn != null) StartOfflineServerBtn.IsEnabled = true;
                    if (StopOfflineServerBtn != null) StopOfflineServerBtn.IsEnabled = true;
                    break;
                case OfflineServerState.Failed:
                    OfflineServerStateText.Text = $"Lỗi: {OfflineTranslationServerManager.LastErrorMessage}";
                    OfflineServerStateText.Foreground = Avalonia.Media.Brushes.Red;
                    if (StartOfflineServerBtn != null) StartOfflineServerBtn.IsEnabled = true;
                    if (StopOfflineServerBtn != null) StopOfflineServerBtn.IsEnabled = true;
                    break;
            }
        }

        private async void StartOfflineServerBtn_Click(object? sender, RoutedEventArgs e)
        {
            string url = OfflineTranslateUrlBox.Text ?? "";
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                OfflineTranslationServerManager.ServerPort = uri.Port;
            }
            await OfflineTranslationServerManager.StartServerAsync();
        }

        private void StopOfflineServerBtn_Click(object? sender, RoutedEventArgs e)
        {
            OfflineTranslationServerManager.StopServer();
        }

        private readonly System.Text.StringBuilder _installerLogs = new System.Text.StringBuilder();

        private void OnInstallerLogReceived(string logLine)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                _installerLogs.AppendLine(logLine);
                if (InstallLogBox != null)
                {
                    InstallLogBox.Text = _installerLogs.ToString();
                    InstallLogBox.CaretIndex = InstallLogBox.Text.Length;
                }
            });
        }

        private void OnInstallerProgressChanged(double val)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (InstallProgressBar != null)
                {
                    InstallProgressBar.Value = val;
                }
            });
        }

        private void OnInstallerCompleted(bool success, string msg)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (StartInstallBtn != null) StartInstallBtn.IsEnabled = true;
                if (CancelInstallBtn != null) CancelInstallBtn.IsEnabled = false;

                if (success)
                {
                    // Tự động kiểm tra lại trạng thái server để đồng bộ UI
                    _ = OfflineTranslationServerManager.StartServerAsync();
                }
            });
        }

        private void StartInstallBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (StartInstallBtn == null || CancelInstallBtn == null || InstallModelCombo == null || InstallLogBox == null) return;

            _installerLogs.Clear();
            InstallLogBox.Text = "";
            StartInstallBtn.IsEnabled = false;
            CancelInstallBtn.IsEnabled = true;

            string modelId = "facebook/nllb-200-distilled-600m";
            string modelOutputDir = "models/nllb-600m-int8";

            if (InstallModelCombo.SelectedIndex == 1)
            {
                modelId = "Helsinki-NLP/opus-mt-en-vi";
                modelOutputDir = "models/opus-en-vi-int8";
            }

            // Đảm bảo cập nhật UI states của nút start/stop server
            if (StartOfflineServerBtn != null) StartOfflineServerBtn.IsEnabled = false;
            if (StopOfflineServerBtn != null) StopOfflineServerBtn.IsEnabled = false;

            _ = OfflineTranslationInstaller.StartInstallAsync(modelId, modelOutputDir);
        }

        private void CancelInstallBtn_Click(object? sender, RoutedEventArgs e)
        {
            OfflineTranslationInstaller.Cancel();
        }
    }
}
