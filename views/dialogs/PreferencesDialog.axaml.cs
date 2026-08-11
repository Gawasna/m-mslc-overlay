using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using m_mslc_overlay.services;
using MMslcOverlay.Services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class PreferencesDialog : Window
    {
        private bool _isUpdatingToggle = false;

        public PreferencesDialog()
        {
            InitializeComponent();
            ConfigManager.Load();
            LoadSettings();

            OfflineTranslationServerManager.OnStateChanged += OnServerStateChanged;
            DiarizerProcessManager.OnGlobalStateChanged += OnAtom32StateChanged;

            this.Closed += (s, e) => {
                OfflineTranslationServerManager.OnStateChanged -= OnServerStateChanged;
                DiarizerProcessManager.OnGlobalStateChanged -= OnAtom32StateChanged;
            };

            this.KeyDown += (s, e) => {
                if (e.Key == Avalonia.Input.Key.S && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
                {
                    SaveSettings();
                    this.Close();
                }
            };
        }

        private void OnAtom32StateChanged(DiarizerState state)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                UpdateAtom32StateUI(state);
            });
        }

        private void OnServerStateChanged(OfflineServerState state)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                UpdateServerStateUI(state);
            });
        }

        public System.Collections.ObjectModel.ObservableCollection<m_mslc_overlay.core.models.HotkeyItem> ConfigurableHotkeys { get; set; } = new();

        private void LoadSettings()
        {
            var cfg = ConfigManager.Current;
            StartupCheck.IsChecked = cfg.RunAtStartup;
            TrayIconCheck.IsChecked = cfg.StartMinimizedToTray;
            CheckUpdatesCheck.IsChecked = cfg.CheckForUpdates;
            
            LanguageCombo.SelectedIndex = cfg.Language == "vi-VN" ? 0 : 1;
            
            if (cfg.TranslationEngine == "DeepL API")
                if (EngineDeepLRadio != null) EngineDeepLRadio.IsChecked = true;
            else if (cfg.TranslationEngine == "Offline CTranslate2")
                if (EngineOfflineRadio != null) EngineOfflineRadio.IsChecked = true;
            else
                if (EngineCloudAIRadio != null) EngineCloudAIRadio.IsChecked = true;
            DeepLApiKeyBox.Text = cfg.DeepLApiKey;
            DeepLContextWindowSizeBox.Value = cfg.DeepLContextWindowSize;
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

            ConfigurableHotkeys.Clear();
            if (cfg.Hotkeys != null)
            {
                foreach (var kvp in cfg.Hotkeys)
                {
                    ConfigurableHotkeys.Add(new m_mslc_overlay.core.models.HotkeyItem(kvp.Value.ActionId, kvp.Value.ActionName, kvp.Value.KeyGesture, kvp.Value.IsGlobal));
                }
            }
            if (HotkeysItemsControl != null) HotkeysItemsControl.ItemsSource = ConfigurableHotkeys;

            if (cfg.OfflineModel == "OPUS-MT")
            {
                if (UseOpusRadio != null) UseOpusRadio.IsChecked = true;
            }
            else
            {
                if (UseNllbRadio != null) UseNllbRadio.IsChecked = true;
            }

            if (UtilAtom32Toggle != null) UtilAtom32Toggle.IsChecked = cfg.EnableDiarizer;

            // Gemini Summary settings
            if (GeminiApiKeyBox != null)        GeminiApiKeyBox.Text = cfg.GeminiApiKey;
            if (SummaryTriggerSegmentsBox != null) SummaryTriggerSegmentsBox.Value = cfg.SummaryTriggerSegments;
            if (SummaryTriggerWordsBox != null) SummaryTriggerWordsBox.Value = cfg.SummaryTriggerWords;
            if (SummaryTriggerTimeBox != null)  SummaryTriggerTimeBox.Value  = cfg.SummaryTriggerTimeSeconds;

            // Set the correct trigger mode RadioButton
            switch (cfg.SummaryTriggerMode)
            {
                case SummaryTriggerMode.ByWords:
                    if (TriggerModeWords != null) TriggerModeWords.IsChecked = true;
                    break;
                case SummaryTriggerMode.ByTime:
                    if (TriggerModeTime != null) TriggerModeTime.IsChecked = true;
                    break;
                default:
                    if (TriggerModeSegments != null) TriggerModeSegments.IsChecked = true;
                    break;
            }

            UpdateServerStateUI(OfflineTranslationServerManager.State);
            UpdateAtom32StateUI(DiarizerProcessManager.GlobalState);
        }

        private void SaveSettings()
        {
            var cfg = ConfigManager.Current;
            string oldEngine = cfg.TranslationEngine;

            cfg.RunAtStartup = StartupCheck.IsChecked ?? false;
            cfg.StartMinimizedToTray = TrayIconCheck.IsChecked ?? true;
            cfg.CheckForUpdates = CheckUpdatesCheck.IsChecked ?? true;
            
            cfg.Language = LanguageCombo.SelectedIndex == 0 ? "vi-VN" : "en-US";
            
            cfg.TranslationEngine = (EngineOfflineRadio?.IsChecked == true) ? "Offline CTranslate2" :
                                    (EngineDeepLRadio?.IsChecked == true) ? "DeepL API" : 
                                    "Cloud AI (Ollama/Gemini)";
            cfg.DeepLApiKey = DeepLApiKeyBox.Text ?? "";
            cfg.DeepLContextWindowSize = Math.Clamp((int)(DeepLContextWindowSizeBox.Value ?? 3), 0, 10);
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
            cfg.EnableDiarizer = UtilAtom32Toggle?.IsChecked ?? false;

            // Gemini Summary settings
            cfg.GeminiApiKey = GeminiApiKeyBox?.Text ?? "";
            cfg.SummaryTriggerSegments = (int)(SummaryTriggerSegmentsBox?.Value ?? 10);
            cfg.SummaryTriggerWords    = (int)(SummaryTriggerWordsBox?.Value ?? 200);
            cfg.SummaryTriggerTimeSeconds = (int)(SummaryTriggerTimeBox?.Value ?? 120);

            // Derive mode from which RadioButton is checked
            if (TriggerModeWords?.IsChecked == true)
                cfg.SummaryTriggerMode = SummaryTriggerMode.ByWords;
            else if (TriggerModeTime?.IsChecked == true)
                cfg.SummaryTriggerMode = SummaryTriggerMode.ByTime;
            else
                cfg.SummaryTriggerMode = SummaryTriggerMode.BySegments;
            
            if (cfg.Hotkeys == null)
            {
                cfg.Hotkeys = new System.Collections.Generic.Dictionary<string, m_mslc_overlay.core.models.HotkeyItem>();
            }
            foreach (var item in ConfigurableHotkeys)
            {
                cfg.Hotkeys[item.ActionId] = item;
            }

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
            if (TabUtilities != null) TabUtilities.IsVisible = false;
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
                    if (TabUtilities != null) TabUtilities.IsVisible = true;
                    break;
                case 5:
                    if (TabAdvanced != null) TabAdvanced.IsVisible = true;
                    break;
                case 6:
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
            _isUpdatingToggle = true;
            try
            {
                string stateText = "Đã dừng";
                var brush = Avalonia.Media.Brushes.Gray;
                bool isToggleChecked = false;
                bool isToggleEnabled = true;

                switch (state)
                {
                    case OfflineServerState.Stopped:
                        stateText = "Đã dừng";
                        brush = Avalonia.Media.Brushes.Gray;
                        isToggleChecked = false;
                        isToggleEnabled = true;
                        break;
                    case OfflineServerState.Starting:
                        stateText = "Đang khởi động...";
                        brush = Avalonia.Media.Brushes.Orange;
                        isToggleChecked = true;
                        isToggleEnabled = false;
                        break;
                    case OfflineServerState.Ready:
                        stateText = "Sẵn sàng (Đang chạy)";
                        brush = Avalonia.Media.Brushes.Green;
                        isToggleChecked = true;
                        isToggleEnabled = true;
                        break;
                    case OfflineServerState.ModelMissing:
                        stateText = "Thiếu mô hình (Model Missing)";
                        brush = Avalonia.Media.Brushes.Red;
                        isToggleChecked = false;
                        isToggleEnabled = true;
                        break;
                    case OfflineServerState.Failed:
                        stateText = $"Lỗi: {OfflineTranslationServerManager.LastErrorMessage}";
                        brush = Avalonia.Media.Brushes.Red;
                        isToggleChecked = false;
                        isToggleEnabled = true;
                        break;
                }

                if (OfflineServerStateText != null) { OfflineServerStateText.Text = stateText; OfflineServerStateText.Foreground = brush; }
                if (UtilAtom26StateText != null) { UtilAtom26StateText.Text = stateText; UtilAtom26StateText.Foreground = brush; }

                if (OfflineServerToggle != null) { OfflineServerToggle.IsChecked = isToggleChecked; OfflineServerToggle.IsEnabled = isToggleEnabled; }
                if (UtilAtom26Toggle != null) { UtilAtom26Toggle.IsChecked = isToggleChecked; UtilAtom26Toggle.IsEnabled = isToggleEnabled; }
            }
            finally
            {
                _isUpdatingToggle = false;
            }
        }

        private void UpdateAtom32StateUI(DiarizerState state)
        {
            string stateText = "Đã dừng";
            var brush = Avalonia.Media.Brushes.Gray;

            switch (state)
            {
                case DiarizerState.Stopped:
                    stateText = "Đã dừng";
                    brush = Avalonia.Media.Brushes.Gray;
                    break;
                case DiarizerState.Starting:
                    stateText = "Đang khởi động...";
                    brush = Avalonia.Media.Brushes.Orange;
                    break;
                case DiarizerState.Ready:
                    stateText = "Sẵn sàng (Đang chạy)";
                    brush = Avalonia.Media.Brushes.Green;
                    break;
                case DiarizerState.Failed:
                    stateText = "Lỗi khởi chạy";
                    brush = Avalonia.Media.Brushes.Red;
                    break;
            }

            if (UtilAtom32StateText != null) 
            { 
                UtilAtom32StateText.Text = stateText; 
                UtilAtom32StateText.Foreground = brush; 
            }
        }

        private async void OfflineServerToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            if (_isUpdatingToggle) return;

            var toggle = sender as ToggleSwitch;
            bool wantsOn = toggle?.IsChecked ?? false;
            if (wantsOn)
            {
                if (OfflineTranslationServerManager.State == OfflineServerState.Stopped ||
                    OfflineTranslationServerManager.State == OfflineServerState.Failed ||
                    OfflineTranslationServerManager.State == OfflineServerState.ModelMissing)
                {
                    string url = OfflineTranslateUrlBox?.Text ?? "http://127.0.0.1:11435";
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        OfflineTranslationServerManager.ServerPort = uri.Port;
                    }
                    await LoadingDialog.ShowLoadingTaskAsync(this, "Đang khởi động Offline Server...\nLần đầu load mô hình có thể tốn từ 10-45 giây.", async (dlg) => {
                        await OfflineTranslationServerManager.StartServerAsync();
                    });
                }
            }
            else
            {
                if (OfflineTranslationServerManager.State == OfflineServerState.Ready ||
                    OfflineTranslationServerManager.State == OfflineServerState.Starting)
                {
                    OfflineTranslationServerManager.StopServer();
                }
            }
        }

        // --- ACTIVE MODEL SELECTION (TAB DỊCH THUẬT) ---
        private void ActiveModelRadio_Checked(object? sender, RoutedEventArgs e)
        {
            if (UseOpusRadio != null && UseOpusRadio.IsChecked == true)
            {
                ConfigManager.Current.OfflineModel = "OPUS-MT";
            }
            else if (UseNllbRadio != null && UseNllbRadio.IsChecked == true)
            {
                ConfigManager.Current.OfflineModel = "NLLB-200 600M";
            }
            ConfigManager.Save();
        }

        // --- TAB TIỆN ÍCH (UTILITIES) ---

        // Dịch thuật Offline
        private async void UtilTransDownloadBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (UtilTransModelCombo == null) return;

            string modelId = "facebook/nllb-200-distilled-600m";
            string modelOutputDir = "models/nllb-600m-int8";

            if (UtilTransModelCombo.SelectedIndex == 1)
            {
                modelId = "Helsinki-NLP/opus-mt-en-vi";
                modelOutputDir = "models/opus-en-vi-int8";
            }

            // Tải/Cài đặt model
            var installDlg = new InstallationDialog(modelId, modelOutputDir);
            await installDlg.ShowDialog(this);
        }

        private void UtilTransDeleteBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (UtilTransModelCombo == null) return;
            string modelDir = UtilTransModelCombo.SelectedIndex == 1 ? "opus-en-vi-int8" : "nllb-600m-int8";
            DeleteModelFolder(modelDir);
        }

        // Speaker Diarization
        private async void UtilSpeakerDownloadBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Placeholder cho Speaker Labeling models
            await MessageDialog.ShowAsync(this, "Thông báo", "Tính năng cài đặt mô hình Speaker Diarization đang được hoàn thiện.");
        }

        private void UtilSpeakerDeleteBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Placeholder
        }

        private void UtilAtom32Toggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            if (_isUpdatingToggle || UtilAtom32Toggle == null) return;
            ConfigManager.Current.EnableDiarizer = UtilAtom32Toggle.IsChecked ?? false;
            LoggerService.Log($"[PreferencesDialog] atom32 (Speaker Diarization) EnableDiarizer toggled to {ConfigManager.Current.EnableDiarizer}");
        }

        private void DeleteModelFolder(string modelDirName)
        {
            try
            {
                string serverDir = OfflineTranslationServerManager.FindServerDirectory();
                if (string.IsNullOrEmpty(serverDir))
                {
                    string configuredPath = ConfigManager.Current.OfflineServerDir;
                    serverDir = Path.IsPathRooted(configuredPath) 
                        ? configuredPath 
                        : AppPathHelper.GetWritablePath(configuredPath);
                }
                string path = Path.Combine(serverDir, "models", modelDirName);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    LoggerService.Log($"[PreferencesDialog] Deleted model folder: {path}");
                    _ = MessageDialog.ShowAsync(this, "Thành công", $"Đã xóa mô hình: {modelDirName}");
                }
                else 
                {
                    _ = MessageDialog.ShowAsync(this, "Thông báo", $"Không tìm thấy thư mục mô hình: {modelDirName}");
                }
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[PreferencesDialog] Error deleting model folder {modelDirName}: {ex.Message}");
                _ = MessageDialog.ShowAsync(this, "Lỗi", $"Lỗi khi xóa mô hình: {ex.Message}");
            }
        }

        private async void RunEnvCheckBtn_Click(object? sender, RoutedEventArgs e)
        {
            await EnvironmentCheckDialog.ShowDiagnosticAsync(this);
        }

        private async void TestGeminiKeyBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (GeminiKeyStatusText == null || GeminiApiKeyBox == null) return;

            GeminiKeyStatusText.Text = "Đang kiểm tra...";
            GeminiKeyStatusText.Foreground = Avalonia.Media.Brushes.Gray;

            // Temporarily apply the entered key for the test
            string originalKey = ConfigManager.Current.GeminiApiKey;
            ConfigManager.Current.GeminiApiKey = GeminiApiKeyBox.Text?.Trim() ?? "";

            try
            {
                using var svc = new m_mslc_overlay.services.GeminiSummaryService();
                bool ok = await svc.TryRequestSummaryAsync(isAutomatic: false);

                GeminiKeyStatusText.Text = ok
                    ? "API key hợp lệ. Kết nối thành công."
                    : "Lỗi: Không thể kết nối. Kiểm tra lại key.";
                GeminiKeyStatusText.Foreground = ok
                    ? Avalonia.Media.Brushes.Green
                    : Avalonia.Media.Brushes.Red;
            }
            catch (Exception ex)
            {
                GeminiKeyStatusText.Text = $"Lỗi: {ex.Message}";
                GeminiKeyStatusText.Foreground = Avalonia.Media.Brushes.Red;
            }
            finally
            {
                // Restore original key (user must click Save to commit)
                ConfigManager.Current.GeminiApiKey = originalKey;
            }
        }

        private void HotkeyInput_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (sender is TextBox textBox && textBox.Tag is string actionId)
            {
                e.Handled = true;

                // Ignore bare modifier keys
                if (e.Key == Avalonia.Input.Key.LeftCtrl || e.Key == Avalonia.Input.Key.RightCtrl ||
                    e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift ||
                    e.Key == Avalonia.Input.Key.LeftAlt || e.Key == Avalonia.Input.Key.RightAlt ||
                    e.Key == Avalonia.Input.Key.LWin || e.Key == Avalonia.Input.Key.RWin)
                {
                    return;
                }

                // Ignore LWin/RWin combinations as requested
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
                {
                    return;
                }

                string modifierString = "";
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) modifierString += "Ctrl+";
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) modifierString += "Alt+";
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) modifierString += "Shift+";

                string keyString = e.Key.ToString();
                string finalGesture = modifierString + keyString;

                // Update the ObservableCollection
                foreach (var item in ConfigurableHotkeys)
                {
                    if (item.ActionId == actionId)
                    {
                        // We must remove and insert to trigger UI update properly if not using INotifyPropertyChanged
                        int index = ConfigurableHotkeys.IndexOf(item);
                        if (index >= 0)
                        {
                            var newItem = new m_mslc_overlay.core.models.HotkeyItem(item.ActionId, item.ActionName, finalGesture, item.IsGlobal);
                            ConfigurableHotkeys[index] = newItem;
                        }
                        break;
                    }
                }
            }
        }
    }
}
