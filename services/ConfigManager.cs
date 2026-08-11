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

        public System.Collections.Generic.Dictionary<string, m_mslc_overlay.core.models.HotkeyItem> Hotkeys { get; set; } = new();

        // "System" | "Light" | "Dark"
        public string ThemeMode { get; set; } = "System";

        // ── Overlay Settings ────────────────────────────────────────────────────────
        public bool OverlayIsLocked { get; set; } = false;
        public double OverlayFontSize { get; set; } = 20.0;
        public string OverlayBackground { get; set; } = "#CC202020";
        public string OverlayTextColor { get; set; } = "#E5E5E5";
        public string OverlayFontFamily { get; set; } = "Segoe UI";
        
        public int OverlayPositionX { get; set; } = -1;
        public int OverlayPositionY { get; set; } = -1;
        public double OverlayWidth { get; set; } = 600;
        public double OverlayHeight { get; set; } = 255;

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

        // ── Gemini Summary Service ──────────────────────────────────────────────
        /// <summary>
        /// API Key cho Gemini Flash 2.5 (tóm tắt nội dung). Khác với ApiKey dùng cho translation.
        /// </summary>
        public string GeminiApiKey { get; set; } = "";

        /// <summary>
        /// Chế độ auto-trigger: BySegments, ByWords, hoặc ByTime.
        /// </summary>
        public SummaryTriggerMode SummaryTriggerMode { get; set; } = SummaryTriggerMode.BySegments;

        /// <summary>
        /// Số segment mới để auto-trigger (dùng khi Mode = BySegments). 0 = tắt.
        /// </summary>
        public int SummaryTriggerSegments { get; set; } = 10;

        /// <summary>
        /// Số từ mới để auto-trigger (dùng khi Mode = ByWords). 0 = tắt.
        /// </summary>
        public int SummaryTriggerWords { get; set; } = 200;

        /// <summary>
        /// Số giây trôi qua để auto-trigger (dùng khi Mode = ByTime). 0 = tắt.
        /// </summary>
        public int SummaryTriggerTimeSeconds { get; set; } = 120;
    }

    /// <summary>
    /// Chế độ kích hoạt tự động của Gemini Summary Service.
    /// Chỉ một chế độ hoạt động tại một thời điểm.
    /// </summary>
    public enum SummaryTriggerMode
    {
        BySegments,
        ByWords,
        ByTime
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
                        EnsureDefaultHotkeys();
                    }
                }
                else
                {
                    EnsureDefaultHotkeys();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load config: {ex.Message}");
                EnsureDefaultHotkeys();
            }
        }

        private static void EnsureDefaultHotkeys()
        {
            if (Current.Hotkeys == null)
            {
                Current.Hotkeys = new System.Collections.Generic.Dictionary<string, m_mslc_overlay.core.models.HotkeyItem>();
            }
            
            var defaults = new System.Collections.Generic.Dictionary<string, m_mslc_overlay.core.models.HotkeyItem>
            {
                { "NewWorkspace", new m_mslc_overlay.core.models.HotkeyItem("NewWorkspace", "Mở Workspace Mới", "Ctrl+Shift+N", false) },
                { "OpenWorkspace", new m_mslc_overlay.core.models.HotkeyItem("OpenWorkspace", "Mở Workspace", "Ctrl+Shift+O", false) },
                { "StartSession", new m_mslc_overlay.core.models.HotkeyItem("StartSession", "Bắt đầu / Dừng Session", "Alt+Shift+R", false) },
                { "ToggleOverlay", new m_mslc_overlay.core.models.HotkeyItem("ToggleOverlay", "Ẩn / Hiện Overlay", "Alt+Shift+O", true) },
                { "ToggleTranslate", new m_mslc_overlay.core.models.HotkeyItem("ToggleTranslate", "Bật / Tắt dịch thuật", "Alt+Shift+T", true) },
                { "CycleLanguage", new m_mslc_overlay.core.models.HotkeyItem("CycleLanguage", "Chuyển đổi ngôn ngữ", "Alt+Shift+L", true) },
                { "ClearText", new m_mslc_overlay.core.models.HotkeyItem("ClearText", "Xoá chữ trên Overlay", "Alt+Shift+C", true) },
                { "FontSizeUp", new m_mslc_overlay.core.models.HotkeyItem("FontSizeUp", "Tăng cỡ chữ", "Alt+Shift+Up", true) },
                { "FontSizeDown", new m_mslc_overlay.core.models.HotkeyItem("FontSizeDown", "Giảm cỡ chữ", "Alt+Shift+Down", true) }
            };

            foreach (var kvp in defaults)
            {
                if (!Current.Hotkeys.ContainsKey(kvp.Key))
                {
                    Current.Hotkeys[kvp.Key] = kvp.Value;
                }
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
