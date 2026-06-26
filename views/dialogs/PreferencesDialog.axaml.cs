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
        }

        private void SaveSettings()
        {
            var cfg = ConfigManager.Current;
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
    }
}
