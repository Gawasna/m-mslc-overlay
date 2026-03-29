using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    public class AIService
    {
        private readonly HttpClient _httpClient;
        public event Action<string>? OnTranslationCompleted;
        public string ContextTopic { get; set; } = "Game/Phim";

        public AIService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task TranslateSentenceAsync(string originalText)
        {
            if (string.IsNullOrWhiteSpace(originalText)) return;

            try
            {
                var requestBody = new
                {
                    model = "qwen2.5:3b",
                    messages = new[]
                    {
                        new { role = "user", content = $"Dịch thuật ngữ cảnh {ContextTopic}, chỉ trả về kết quả only:\n\n{originalText}" }
                    },
                    stream = false
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Gọi tới endpoint tiêu chuẩn của Ollama chạy ở local
                var response = await _httpClient.PostAsync("http://127.0.0.1:11434/api/chat", content);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(responseString);
                var translatedText = jsonDoc.RootElement
                                      .GetProperty("message")
                                      .GetProperty("content")
                                      .GetString();

                if (!string.IsNullOrWhiteSpace(translatedText))
                {
                    OnTranslationCompleted?.Invoke(translatedText.Trim());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIService] Lỗi gọi Ollama API: {ex.Message}");
            }
        }
    }
}
