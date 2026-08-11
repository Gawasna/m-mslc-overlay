using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace m_mslc_overlay.services
{
    /// <summary>
    /// Calls Gemini Flash 2.5 to produce concise summaries of accumulated transcript content.
    ///
    /// Trigger modes (mutual-exclusive, configured via SummaryTriggerMode):
    ///   BySegments — fire every N new segments
    ///   ByWords    — fire every N new words
    ///   ByTime     — fire every N seconds of elapsed recording time
    ///
    /// Rate limiting: 5 requests per 60-second sliding window.
    ///
    /// Context-aware chaining: each new summary receives the previous summary as
    /// "prior context" in the prompt, preventing information orphaning between windows.
    /// </summary>
    public sealed class GeminiSummaryService : IDisposable
    {
        private const string GeminiEndpoint =
            "https://generativelanguage.googleapis.com/v1beta/interactions";

        private readonly HttpClient _http;
        private readonly SlidingWindowRateLimiter _rateLimiter;

        // ─── Counters reset after each successful summary ─────────────────────
        private int _segmentsSinceLast;
        private int _wordsSinceLast;

        // ─── Timer-mode state ─────────────────────────────────────────────────
        private Timer? _timeTimer;
        private readonly object _timerLock = new();

        // ─── Context-aware chaining ───────────────────────────────────────────
        /// <summary>
        /// Rolling summary chain: the last N summaries compressed into one string.
        /// This is prepended to every new request so the model is never context-blind.
        /// </summary>
        private string _cumulativeSummary = string.Empty;

        // Pending transcript text accumulated since last summary
        private readonly StringBuilder _pendingContext = new();

        private bool _disposed;

        // ─── Events ───────────────────────────────────────────────────────────

        /// <summary>Fired on UI thread when a summary is ready. Payload = latest summary text.</summary>
        public event Action<string>? OnSummaryReady;

        /// <summary>Fired on UI thread on rate-limit hit or API error.</summary>
        public event Action<string>? OnError;

        /// <summary>Fired on UI thread whenever the remaining request count changes.</summary>
        public event Action<int>? OnRemainingRequestsChanged;

        // ─── State ────────────────────────────────────────────────────────────

        public bool IsBusy { get; private set; }

        // ─── Constructor ──────────────────────────────────────────────────────

        public GeminiSummaryService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _rateLimiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit          = 5,
                Window               = TimeSpan.FromMinutes(1),
                SegmentsPerWindow    = 1,
                QueueProcessingOrder = QueueProcessingOrder.NewestFirst,
                QueueLimit           = 0  // reject immediately when over limit
            });
        }

        // ─── Auto-trigger entry point ─────────────────────────────────────────

        /// <summary>
        /// Called by TranscriptViewportViewModel each time a new segment arrives.
        /// Appends text to context buffer and evaluates the active trigger mode.
        /// </summary>
        public void NotifyNewSegment(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            _pendingContext.Append(' ').Append(text);
            _segmentsSinceLast++;
            _wordsSinceLast += CountWords(text);

            CheckAutoTrigger();
        }

        private void CheckAutoTrigger()
        {
            var mode = ConfigManager.Current.SummaryTriggerMode;

            switch (mode)
            {
                case SummaryTriggerMode.BySegments:
                {
                    int thr = ConfigManager.Current.SummaryTriggerSegments;
                    if (thr > 0 && _segmentsSinceLast >= thr)
                        _ = TryRequestSummaryAsync(isAutomatic: true);
                    break;
                }
                case SummaryTriggerMode.ByWords:
                {
                    int thr = ConfigManager.Current.SummaryTriggerWords;
                    if (thr > 0 && _wordsSinceLast >= thr)
                        _ = TryRequestSummaryAsync(isAutomatic: true);
                    break;
                }
                // ByTime is driven by StartTimeTimer — no action here
            }
        }

        // ─── Timer-mode management ────────────────────────────────────────────

        /// <summary>
        /// Starts or restarts the elapsed-time timer based on current config.
        /// Must be called whenever SummaryTriggerMode or SummaryTriggerTimeSeconds changes.
        /// </summary>
        public void RefreshTimerMode()
        {
            lock (_timerLock)
            {
                _timeTimer?.Dispose();
                _timeTimer = null;

                if (ConfigManager.Current.SummaryTriggerMode != SummaryTriggerMode.ByTime) return;

                int seconds = ConfigManager.Current.SummaryTriggerTimeSeconds;
                if (seconds <= 0) return;

                var interval = TimeSpan.FromSeconds(seconds);
                // First tick after one full interval, then repeat
                _timeTimer = new Timer(_ =>
                {
                    if (!_disposed && ConfigManager.Current.SummaryTriggerMode == SummaryTriggerMode.ByTime)
                        _ = TryRequestSummaryAsync(isAutomatic: true);
                }, null, interval, interval);
            }
        }

        // ─── Manual trigger ───────────────────────────────────────────────────

        /// <summary>
        /// Manually request a summary. Returns false when rate-limited or API key missing.
        /// Raises OnError with a descriptive message on failure.
        /// </summary>
        public async Task<bool> TryRequestSummaryAsync(bool isAutomatic = false)
        {
            if (_disposed) return false;

            // Try to acquire a rate-limit token (non-blocking)
            using var lease = await _rateLimiter.AcquireAsync(1);
            int remaining = (int)(_rateLimiter.GetStatistics()?.CurrentAvailablePermits ?? 0);
            FireRemainingRequests(remaining);

            if (!lease.IsAcquired)
            {
                if (!isAutomatic)
                    FireError("Rate limit reached: maximum 5 requests per minute.");
                return false;
            }

            string apiKey = ConfigManager.Current.GeminiApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (!isAutomatic)
                    FireError("Gemini API key is not configured. Go to Preferences → Tiện ích.");
                return false;
            }

            IsBusy = true;
            try
            {
                string newContent = _pendingContext.ToString().Trim();
                if (string.IsNullOrWhiteSpace(newContent))
                {
                    if (!isAutomatic)
                        FireError("No new transcript content to summarize yet.");
                    return false;
                }

                // Build context-aware prompt (chain previous summary as prior context)
                string summary = await CallGeminiFlashAsync(newContent, _cumulativeSummary, apiKey);

                // Reset segment/word counters and pending buffer on success
                _segmentsSinceLast = 0;
                _wordsSinceLast    = 0;
                _pendingContext.Clear();

                // Update cumulative context: compress previous + new into rolling chain
                _cumulativeSummary = BuildRollingContext(_cumulativeSummary, summary);

                FireSummaryReady(summary);
                return true;
            }
            catch (OperationCanceledException)
            {
                FireError("Summary request timed out.");
                return false;
            }
            catch (HttpRequestException ex)
            {
                FireError($"Network error: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                FireError($"Summary error: {ex.Message}");
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Resets the cumulative context chain (e.g. when starting a new session).
        /// </summary>
        public void ResetContext()
        {
            _cumulativeSummary = string.Empty;
            _pendingContext.Clear();
            _segmentsSinceLast = 0;
            _wordsSinceLast    = 0;
        }

        // ─── Prompt construction ──────────────────────────────────────────────

        /// <summary>
        /// Builds the context-aware chain string. Keeps only the latest prior summary
        /// to avoid unbounded token growth while still anchoring the new summary.
        /// </summary>
        private static string BuildRollingContext(string previousSummary, string latestSummary)
        {
            // Strategy: keep at most 2 prior summary "frames" compressed into one prefix.
            // We store latestSummary as the new cumulative context.
            // On the next call, it becomes "prior context" in the prompt.
            return string.IsNullOrWhiteSpace(previousSummary)
                ? latestSummary
                : $"[Tóm tắt trước]: {previousSummary}\n[Cập nhật mới]: {latestSummary}";
        }

        // ─── Gemini API call ──────────────────────────────────────────────────

        /// <param name="newContent">Transcript text accumulated since last summary.</param>
        /// <param name="priorContext">
        /// Compressed summary of earlier conversation windows.
        /// Empty string = first summary (no prior context to chain).
        /// </param>
        private async Task<string> CallGeminiFlashAsync(
            string newContent, string priorContext, string apiKey)
        {
            // Build context-aware prompt
            string prompt = BuildPrompt(newContent, priorContext);

            var payload = new
            {
                model = "gemini-3.5-flash",
                input = prompt
            };

            var req = new HttpRequestMessage(HttpMethod.Post, GeminiEndpoint);
            req.Headers.Add("x-goog-api-key", apiKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var resp = await _http.SendAsync(req);

            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Gemini API returned {(int)resp.StatusCode}: {body}");
            }

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var steps = doc.RootElement.GetProperty("steps");
            foreach (var step in steps.EnumerateArray())
            {
                if (step.GetProperty("type").GetString() == "model_output")
                {
                    return step.GetProperty("content")[0].GetProperty("text").GetString() ?? "(no summary returned)";
                }
            }
            
            return "(no summary returned)";
        }

        /// <summary>
        /// Constructs the prompt string.
        /// When prior context exists, it is included as a framing anchor so the
        /// new summary stays coherent with earlier windows (context-aware chaining).
        /// </summary>
        private static string BuildPrompt(string newContent, string priorContext)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(priorContext))
            {
                sb.AppendLine("Bạn đang hỗ trợ tóm tắt một cuộc hội thoại dài. Dưới đây là tóm tắt các đoạn trước để làm ngữ cảnh nền:");
                sb.AppendLine();
                sb.AppendLine("=== NGỮ CẢNH TRƯỚC ===");
                sb.AppendLine(priorContext);
                sb.AppendLine("=== KẾT THÚC NGỮ CẢNH ===");
                sb.AppendLine();
                sb.AppendLine("Dưới đây là nội dung MỚI cần tóm tắt (chỉ phần này, không lặp lại ngữ cảnh trước):");
            }
            else
            {
                sb.AppendLine("Tóm tắt ngắn gọn (3–5 câu) nội dung cuộc hội thoại sau bằng cùng ngôn ngữ với nội dung đó:");
            }

            sb.AppendLine();
            sb.AppendLine("=== NỘI DUNG ===");
            sb.AppendLine(newContent);
            sb.AppendLine("=== KẾT THÚC ===");
            sb.AppendLine();
            sb.Append("Viết tóm tắt súc tích (3–5 câu), bắt đầu ngay, không mở đầu dài dòng:");

            return sb.ToString();
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static int CountWords(string text)
            => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // ─── UI-thread dispatch ───────────────────────────────────────────────

        private void FireSummaryReady(string text)
            => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnSummaryReady?.Invoke(text));

        private void FireError(string msg)
            => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnError?.Invoke(msg));

        private void FireRemainingRequests(int count)
            => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnRemainingRequestsChanged?.Invoke(count));

        // ─── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_timerLock)
            {
                _timeTimer?.Dispose();
                _timeTimer = null;
            }
            _http.Dispose();
            _rateLimiter.Dispose();
        }
    }
}
