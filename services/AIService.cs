using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using m_mslc_overlay.core;

namespace m_mslc_overlay.services
{
    public class AIService : IDisposable
    {
        private readonly HttpClient _httpClient;

        // ATOM81: event now carries TranslationResult (includes source CommitMetadata)
        public event Action<TranslationResult>? OnTranslationCompleted;
        public event Action<string>? OnTranslationTokenReceived; // kept as-is (streaming tokens)

        // ATOM81: replaced SemaphoreSlim(1,1) with priority queue
        private readonly TranslationPriorityQueue _priorityQueue = new TranslationPriorityQueue();

        public string ContextTopic { get; set; } = "Game/Phim";
        public string TargetLanguage { get; set; } = "Tiếng Việt";

        public AIService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        // ATOM81: primary entry point — enqueue with priority based on CommitMetadata
        public void EnqueueTranslation(CommitMetadata meta)
        {
            if (string.IsNullOrWhiteSpace(meta.Text)) return;
            _priorityQueue.Enqueue(meta, async (m, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteTranslationAsync(m, ct);
            });
        }

        // Backward-compat overload: wraps bare string into CommitMetadata
        public void EnqueueTranslation(string text, string reason = "SoftCommit")
            => EnqueueTranslation(CommitMetadata.From(text, reason));

        // ATOM81: kept with [Obsolete] so existing callers still compile; routes through priority queue
        [Obsolete("Use EnqueueTranslation instead. This overload wraps into SoftCommit P1.")]
        public Task TranslateSentenceAsync(string originalText)
        {
            EnqueueTranslation(originalText, "SoftCommit");
            return Task.CompletedTask;
        }

        // -----------------------------------------------------------------------
        // Private: dispatch to the right engine
        // -----------------------------------------------------------------------

        private async Task ExecuteTranslationAsync(CommitMetadata meta, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (ConfigManager.Current.TranslationEngine == "DeepL API")
                await TranslateWithDeepLAsync(meta, ct);
            else if (ConfigManager.Current.TranslationEngine == "Offline CTranslate2")
                await TranslateWithOfflineCTranslate2Async(meta, ct);
            else
                await TranslateWithOllamaAsync(meta, ct);
        }

        // -----------------------------------------------------------------------
        // Engine: Offline CTranslate2
        // -----------------------------------------------------------------------

        private async Task TranslateWithOfflineCTranslate2Async(CommitMetadata meta, CancellationToken ct)
        {
            try
            {
                string targetLangCode = TargetLanguage switch {
                    "Tiếng Việt" => "vie_Latn",
                    "Tiếng Nhật" => "jpn_Jpan",
                    "Tiếng Trung" => "zho_Hans",
                    "English" => "eng_Latn",
                    _ => "vie_Latn"
                };

                var requestBody = new
                {
                    text = meta.Text,
                    source_lang = "eng_Latn",
                    target_lang = targetLangCode
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                string url = ConfigManager.Current.OfflineTranslateUrl;
                if (string.IsNullOrWhiteSpace(url))
                    url = "http://127.0.0.1:11435";

                var response = await _httpClient.PostAsync($"{url.TrimEnd('/')}/translate", content, ct);
                response.EnsureSuccessStatusCode();

                string responseStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseStr);

                if (doc.RootElement.TryGetProperty("translated_text", out var translatedTextProp))
                {
                    string translatedText = translatedTextProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(translatedText))
                        OnTranslationCompleted?.Invoke(TranslationResult.From(translatedText.Trim(), meta));
                }
            }
            catch (OperationCanceledException) { throw; } // propagate cancel
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIService] Lỗi gọi Offline CTranslate2: {ex.Message}");
                OnTranslationCompleted?.Invoke(TranslationResult.From($"[Lỗi Offline NLLB: {ex.Message}]", meta, isError: true));
            }
        }

        // -----------------------------------------------------------------------
        // Engine: DeepL API
        // -----------------------------------------------------------------------

        private async Task TranslateWithDeepLAsync(CommitMetadata meta, CancellationToken ct)
        {
            try
            {
                string apiKey = ConfigManager.Current.DeepLApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    string msg = "Lỗi: Chưa cấu hình DeepL API Key.";
                    OnTranslationTokenReceived?.Invoke(msg);
                    OnTranslationCompleted?.Invoke(TranslationResult.From(msg, meta, isError: true));
                    return;
                }

                string endpoint = apiKey.EndsWith(":fx")
                    ? "https://api-free.deepl.com/v2/translate"
                    : "https://api.deepl.com/v2/translate";

                string targetLangCode = TargetLanguage switch {
                    "Tiếng Việt" => "VI",
                    "Tiếng Nhật" => "JA",
                    "Tiếng Trung" => "ZH",
                    "English" => "EN",
                    _ => "VI"
                };

                var requestBody = new
                {
                    text = new[] { meta.Text },
                    target_lang = targetLangCode
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = content
                };
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");

                var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                string responseStr = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseStr);
                var translations = doc.RootElement.GetProperty("translations");

                if (translations.GetArrayLength() > 0)
                {
                    string translatedText = translations[0].GetProperty("text").GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(translatedText))
                        OnTranslationCompleted?.Invoke(TranslationResult.From(translatedText, meta));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIService] Lỗi gọi DeepL API: {ex.Message}");
                OnTranslationCompleted?.Invoke(TranslationResult.From($"[Lỗi DeepL: {ex.Message}]", meta, isError: true));
            }
        }

        // -----------------------------------------------------------------------
        // Engine: Ollama (streaming)
        // -----------------------------------------------------------------------

        private async Task TranslateWithOllamaAsync(CommitMetadata meta, CancellationToken ct)
        {
            try
            {
                var requestBody = new
                {
                    model = "qwen2.5:3b",
                    messages = new[]
                    {
                        new { role = "user", content = $"Dịch sang {TargetLanguage} với ngữ cảnh {ContextTopic}, chỉ trả về duy nhất kết quả dịch, không giải thích gì thêm:\n\n{meta.Text}" }
                    },
                    stream = true
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:11434/api/chat")
                {
                    Content = content
                };
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                var sb = new StringBuilder();

                while (!reader.EndOfStream)
                {
                    ct.ThrowIfCancellationRequested();

                    var line = await reader.ReadLineAsync(ct);
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
                        break;
                }

                if (sb.Length > 0)
                    OnTranslationCompleted?.Invoke(TranslationResult.From(sb.ToString().Trim(), meta));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIService] Lỗi gọi Ollama API: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // IDisposable
        // -----------------------------------------------------------------------

        public void Dispose()
        {
            _priorityQueue.Dispose();
            _httpClient.Dispose();
        }
    }
}
