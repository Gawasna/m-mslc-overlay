using System;
using System.IO;
using System.Text.Json;

namespace m_mslc_overlay.services
{
    public class AppConfig
    {
        // First-run wizard tracking
        public bool HasCompletedFirstRun { get; set; } = false;
        public bool HasCompletedOnboarding { get; set; } = false;
        
        public bool RunAtStartup { get; set; } = false;
        public bool StartMinimizedToTray { get; set; } = true;
        public bool CheckForUpdates { get; set; } = true;
        public string Language { get; set; } = "vi-VN";
        public string ExtractorTag { get; set; } = "";
        
        public string AiModel { get; set; } = "Gemini 1.5 Pro";
        public string ApiKey { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        
        public string TranslationEngine { get; set; } = "Cloud AI (Ollama/Gemini)";
        public string DeepLApiKey { get; set; } = "";
        public int DeepLContextWindowSize { get; set; } = 3;
        public string OfflineTranslateUrl { get; set; } = "http://127.0.0.1:11435";
        public string OfflineServerDir { get; set; } = "plugins/atom26";
        public string OfflineModel { get; set; } = "NLLB-200 600M"; // "NLLB-200 600M" or "OPUS-MT"
        
        public string PipeName { get; set; } = "MSLCCaptionPipe";
        public bool VerboseLogging { get; set; } = false;
        public bool EnableGlobalHotkeys { get; set; } = true;

        // "System" | "Light" | "Dark"
        public string ThemeMode { get; set; } = "System";

        // ── atom32: Speaker Diarization ──────────────────────────────────────────────
        /// <summary>
        /// Bật/tắt Speaker Diarization. Mặc định false để không tốn CPU khi không cần.
        /// </summary>
        public bool EnableDiarizer { get; set; } = false;

        /// <summary>
        /// Audio input device index (0 = default system). Khớp với --device của cli_diarizer.py.
        /// </summary>
        public int DiarizerDeviceIndex { get; set; } = 0;

        /// <summary>
        /// Cosine distance threshold để accept speaker match (0.0–1.0).
        /// Thấp hơn = strict hơn, ít false positive hơn.
        /// </summary>
        public float DiarizerThreshold { get; set; } = 0.5f;

        /// <summary>
        /// Thời lượng tối thiểu (giây) một segment để đưa vào diarization.
        /// Filter bỏ micro-segment nhiễu.
        /// </summary>
        public float DiarizerMinSpeechDuration { get; set; } = 1.2f;

        /// <summary>
        /// Danh sách đường dẫn các Workspace gần đây (Item 16).
        /// </summary>
        public System.Collections.Generic.List<string> RecentWorkspaces { get; set; } = new();
    }

    public static class ConfigManager
    {
        private static readonly string ConfigPath = AppPathHelper.GetConfigFilePath();
        public static AppConfig Current { get; set; } = new AppConfig();

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        Current = config;
                        Current.RecentWorkspaces ??= new System.Collections.Generic.List<string>();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load config: {ex.Message}");
            }
        }

        public static void AddRecentWorkspace(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string fullPath = Path.GetFullPath(path);
                Current.RecentWorkspaces ??= new System.Collections.Generic.List<string>();
                Current.RecentWorkspaces.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
                Current.RecentWorkspaces.Insert(0, fullPath);

                if (Current.RecentWorkspaces.Count > 10)
                {
                    Current.RecentWorkspaces = Current.RecentWorkspaces.GetRange(0, 10);
                }
                Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to add recent workspace: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(Current, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save config: {ex.Message}");
            }
        }
    }
}
