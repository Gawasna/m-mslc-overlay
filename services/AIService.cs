using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace m_mslc_overlay.services
{
    public class AIService
    {
        private readonly HttpClient _httpClient;
        public event Action<string>? OnTranslationCompleted;
        public event Action<string>? OnTranslationTokenReceived;
        
        // Block Concurrent Translation để tránh Token của 2 câu bị trộn lẫn trên đường truyền
        private readonly SemaphoreSlim _translateSemaphore = new SemaphoreSlim(1, 1);

        public string ContextTopic { get; set; } = "Game/Phim";
        public string TargetLanguage { get; set; } = "Tiếng Việt";

        public AIService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task TranslateSentenceAsync(string originalText)
        {
            if (string.IsNullOrWhiteSpace(originalText)) return;

            await _translateSemaphore.WaitAsync();
            try
            {
                if (ConfigManager.Current.TranslationEngine == "DeepL API")
                {
                    await TranslateWithDeepLAsync(originalText);
                }
                else
                {
                    await TranslateWithOllamaAsync(originalText);
                }
            }
            finally
            {
                _translateSemaphore.Release();
            }
        }

        private async Task TranslateWithDeepLAsync(string originalText)
        {
            try
            {
                string apiKey = ConfigManager.Current.DeepLApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    string msg = "Lỗi: Chưa cấu hình DeepL API Key.";
                    OnTranslationTokenReceived?.Invoke(msg);
                    OnTranslationCompleted?.Invoke(msg);
                    return;
                }

                // Determine endpoint based on key type (free keys end with :fx)
                string endpoint = apiKey.EndsWith(":fx") 
                    ? "https://api-free.deepl.com/v2/translate" 
                    : "https://api.deepl.com/v2/translate";

                // Map target language string to DeepL code
                string targetLangCode = TargetLanguage switch {
                    "Tiếng Việt" => "VI",
                    "Tiếng Nhật" => "JA",
                    "Tiếng Trung" => "ZH",
                    "English" => "EN",
                    _ => "VI"
                };

                var requestBody = new
                {
                    text = new[] { originalText },
                    target_lang = targetLangCode
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = content
                };
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string responseStr = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseStr);
                var translations = doc.RootElement.GetProperty("translations");
                
                if (translations.GetArrayLength() > 0)
                {
                    string translatedText = translations[0].GetProperty("text").GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(translatedText))
                    {
                        // Fire only completed so that MainWindow can enqueue for typewriter effect
                        OnTranslationCompleted?.Invoke(translatedText);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIService] Lỗi gọi DeepL API: {ex.Message}");
                string errMsg = $"[Lỗi DeepL: {ex.Message}]";
                OnTranslationCompleted?.Invoke(errMsg);
            }
        }

        private async Task TranslateWithOllamaAsync(string originalText)
        {
            try
            {
                var requestBody = new
                {
                    model = "qwen2.5:3b",
                    messages = new[]
                    {
                        new { role = "user", content = $"Dịch sang {TargetLanguage} với ngữ cảnh {ContextTopic}, chỉ trả về duy nhất kết quả dịch, không giải thích gì thêm:\n\n{originalText}" }
                    },
                    stream = true
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Gửi request streaming
                var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:11434/api/chat")
                {
                    Content = content
                };
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                var sb = new StringBuilder();

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(line)) continue;

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("message", out var msgProp) && msgProp.TryGetProperty("content", out var contentProp))
                    {
                        var token = contentProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(token))
                        {
                            sb.Append(token);
                            OnTranslationTokenReceived?.Invoke(token);
                        }
                    }

                    if (root.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
                    {
                        break;
                    }
                }

                if (sb.Length > 0)
                {
                    OnTranslationCompleted?.Invoke(sb.ToString().Trim());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIService] Lỗi gọi Ollama API: {ex.Message}");
            }
        }
    }
}
