using System;
using System.IO;
using System.Text.Json;

namespace m_mslc_overlay.services
{
    public class AppConfig
    {
        public bool RunAtStartup { get; set; } = false;
        public bool StartMinimizedToTray { get; set; } = true;
        public bool CheckForUpdates { get; set; } = true;
        public string Language { get; set; } = "vi-VN";
        
        public string AiModel { get; set; } = "Gemini 1.5 Pro";
        public string ApiKey { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        
        public string TranslationEngine { get; set; } = "Cloud AI (Ollama/Gemini)";
        public string DeepLApiKey { get; set; } = "";
        public string OfflineTranslateUrl { get; set; } = "http://127.0.0.1:11435";
        public string OfflineServerDir { get; set; } = "plugins/atom26";
        
        public string PipeName { get; set; } = "MSLCCaptionPipe";
        public bool VerboseLogging { get; set; } = false;
        public bool EnableGlobalHotkeys { get; set; } = true;
    }

    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
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
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load config: {ex.Message}");
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
