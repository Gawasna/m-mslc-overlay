using Avalonia;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace m_mslc_overlay.services
{
    public static class LanguageManager
    {
        public static string CurrentLanguage { get; private set; } = "vi-VN";

        public static void LoadLanguage(string langCode)
        {
            try
            {
                var uri = new Uri($"avares://m-mslc-overlay/assets/i18n/{langCode}.json");
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null && Application.Current != null)
                {
                    foreach (var kvp in dict)
                    {
                        Application.Current.Resources[kvp.Key] = kvp.Value;
                    }
                    CurrentLanguage = langCode;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load language {langCode}: {ex.Message}");
            }
        }

        public static string GetString(string key)
        {
            if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var val) && val is string str)
            {
                return str;
            }
            return $"[{key}]";
        }
    }
}
