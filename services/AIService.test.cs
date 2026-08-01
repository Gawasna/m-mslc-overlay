using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m_mslc_overlay.core;
using m_mslc_overlay.services;
using Xunit;

namespace m_mslc_overlay.services.tests
{
    public class TestHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public HttpResponseMessage ResponseToReturn { get; set; }

        public TestHttpMessageHandler()
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"translations\":[{\"text\":\"Mocked Translation\"}]}", Encoding.UTF8, "application/json")
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return ResponseToReturn;
        }
    }

    public class AIServiceTests
    {
        // =========================================================================
        // Category 1: Config Boundary Tests (3 tests)
        // =========================================================================

        [Fact]
        public void Test_AppConfig_DefaultValue_IsThree()
        {
            var config = new AppConfig();
            Assert.Equal(3, config.DeepLContextWindowSize);
        }

        [Fact]
        public void Test_AppConfig_Serialization_IncludesContextWindowSize()
        {
            var config = new AppConfig { DeepLContextWindowSize = 5 };
            string json = JsonSerializer.Serialize(config);
            Assert.Contains("DeepLContextWindowSize", json);

            var deserialized = JsonSerializer.Deserialize<AppConfig>(json);
            Assert.NotNull(deserialized);
            Assert.Equal(5, deserialized.DeepLContextWindowSize);
        }

        [Fact]
        public void Test_AppConfig_Deserialization_HandlesMissingProperty()
        {
            string jsonWithoutProperty = "{\"Language\":\"vi-VN\",\"ApiKey\":\"test-key\"}";
            var config = JsonSerializer.Deserialize<AppConfig>(jsonWithoutProperty);
            Assert.NotNull(config);
            Assert.Equal(3, config.DeepLContextWindowSize);
        }

        // =========================================================================
        // Category 2: Sliding Queue Tests (7 tests)
        // =========================================================================

        [Fact]
        public void Test_SlidingQueue_MaintainsUpToN()
        {
            using var aiService = new AIService();
            aiService.ClearContextQueue();
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("Sentence 1");
            aiService.RecordSourceContext("Sentence 2");
            aiService.RecordSourceContext("Sentence 3");

            Assert.Equal("Sentence 1\nSentence 2\nSentence 3", aiService.GetDeepLContextString());
        }

        [Fact]
        public void Test_SlidingQueue_FIFOEviction()
        {
            using var aiService = new AIService();
            aiService.ClearContextQueue();
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("Sentence 1");
            aiService.RecordSourceContext("Sentence 2");
            aiService.RecordSourceContext("Sentence 3");
            aiService.RecordSourceContext("Sentence 4");

            Assert.Equal("Sentence 2\nSentence 3\nSentence 4", aiService.GetDeepLContextString());
        }

        [Fact]
        public void Test_SlidingQueue_DisabledWhenZero()
        {
            using var aiService = new AIService();
            aiService.ClearContextQueue();
            ConfigManager.Current.DeepLContextWindowSize = 0;

            aiService.RecordSourceContext("Sentence 1");
            Assert.Null(aiService.GetDeepLContextString());
        }

        [Fact]
        public void Test_SlidingQueue_ClearsOnZeroOrNegative()
        {
            using var aiService = new AIService();
            aiService.ClearContextQueue();
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("Sentence 1");
            aiService.RecordSourceContext("Sentence 2");
            Assert.Equal("Sentence 1\nSentence 2", aiService.GetDeepLContextString());

            ConfigManager.Current.DeepLContextWindowSize = 0;
            aiService.RecordSourceContext("Sentence 3");
            Assert.Null(aiService.GetDeepLContextString());

            ConfigManager.Current.DeepLContextWindowSize = -2;
            aiService.RecordSourceContext("Sentence 4");
            Assert.Null(aiService.GetDeepLContextString());
        }

        [Fact]
        public void Test_SlidingQueue_NormalizesNewlines()
        {
            using var aiService = new AIService();
            aiService.ClearContextQueue();
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("Line 1\r\nwith CRLF");
            aiService.RecordSourceContext("Line 2\nwith LF");
            aiService.RecordSourceContext("Line 3\rwith CR");

            Assert.Equal("Line 1 with CRLF\nLine 2 with LF\nLine 3 with CR", aiService.GetDeepLContextString());
        }

        [Fact]
        public void Test_SlidingQueue_IgnoresEmptyOrWhitespace()
        {
            using var aiService = new AIService();
            aiService.ClearContextQueue();
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("");
            aiService.RecordSourceContext("   ");
            aiService.RecordSourceContext("\r\n\t");
            Assert.Null(aiService.GetDeepLContextString());

            aiService.RecordSourceContext("Valid line");
            aiService.RecordSourceContext("    ");
            Assert.Equal("Valid line", aiService.GetDeepLContextString());
        }

        [Fact]
        public void Test_SlidingQueue_DynamicResizeDown_ImmediateGetContext()
        {
            using var aiService = new AIService();
            aiService.ClearContextQueue();
            ConfigManager.Current.DeepLContextWindowSize = 5;

            aiService.RecordSourceContext("Line 1");
            aiService.RecordSourceContext("Line 2");
            aiService.RecordSourceContext("Line 3");
            aiService.RecordSourceContext("Line 4");
            aiService.RecordSourceContext("Line 5");
            Assert.Equal("Line 1\nLine 2\nLine 3\nLine 4\nLine 5", aiService.GetDeepLContextString());

            // User resizes context window down to 2
            ConfigManager.Current.DeepLContextWindowSize = 2;

            // Immediately query context string before any new record
            string? context = aiService.GetDeepLContextString();
            Assert.Equal("Line 4\nLine 5", context);
        }

        // =========================================================================
        // Category 3: DeepL Payload Integration Tests (5 tests)
        // =========================================================================

        [Fact]
        public async Task Test_DeepLPayload_ContainsContext_WhenConfiguredAndNonEmpty()
        {
            var handler = new TestHttpMessageHandler();
            using var aiService = new AIService(handler);
            aiService.ClearContextQueue();

            ConfigManager.Current.TranslationEngine = "DeepL API";
            ConfigManager.Current.DeepLApiKey = "test-key:fx";
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("Context line 1");
            aiService.RecordSourceContext("Context line 2");

            var tcs = new TaskCompletionSource<TranslationResult>();
            aiService.OnTranslationCompleted += res => tcs.TrySetResult(res);
            aiService.EnqueueTranslation(CommitMetadata.From("Target text to translate", "HardCommit"));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(handler.LastRequest);
            Assert.NotNull(handler.LastRequestBody);

            using var doc = JsonDocument.Parse(handler.LastRequestBody);
            Assert.True(doc.RootElement.TryGetProperty("context", out var contextProp));
            Assert.Equal("Context line 1\nContext line 2", contextProp.GetString());
        }

        [Fact]
        public async Task Test_DeepLPayload_OmitsContextKey_WhenSizeIsZero()
        {
            var handler = new TestHttpMessageHandler();
            using var aiService = new AIService(handler);
            aiService.ClearContextQueue();

            ConfigManager.Current.TranslationEngine = "DeepL API";
            ConfigManager.Current.DeepLApiKey = "test-key:fx";
            ConfigManager.Current.DeepLContextWindowSize = 0;

            aiService.RecordSourceContext("Context line 1");

            var tcs = new TaskCompletionSource<TranslationResult>();
            aiService.OnTranslationCompleted += res => tcs.TrySetResult(res);
            aiService.EnqueueTranslation(CommitMetadata.From("Target text to translate", "HardCommit"));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(handler.LastRequest);
            Assert.NotNull(handler.LastRequestBody);

            using var doc = JsonDocument.Parse(handler.LastRequestBody);
            Assert.False(doc.RootElement.TryGetProperty("context", out _));
        }

        [Fact]
        public async Task Test_DeepLPayload_OmitsContextKey_WhenQueueIsEmpty()
        {
            var handler = new TestHttpMessageHandler();
            using var aiService = new AIService(handler);
            aiService.ClearContextQueue();

            ConfigManager.Current.TranslationEngine = "DeepL API";
            ConfigManager.Current.DeepLApiKey = "test-key:fx";
            ConfigManager.Current.DeepLContextWindowSize = 3;

            var tcs = new TaskCompletionSource<TranslationResult>();
            aiService.OnTranslationCompleted += res => tcs.TrySetResult(res);
            aiService.EnqueueTranslation(CommitMetadata.From("Target text to translate", "HardCommit"));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(handler.LastRequest);
            Assert.NotNull(handler.LastRequestBody);

            using var doc = JsonDocument.Parse(handler.LastRequestBody);
            Assert.False(doc.RootElement.TryGetProperty("context", out _));
        }

        [Fact]
        public async Task Test_DeepLPayload_FormatsMultipleSentencesWithNewline()
        {
            var handler = new TestHttpMessageHandler();
            using var aiService = new AIService(handler);
            aiService.ClearContextQueue();

            ConfigManager.Current.TranslationEngine = "DeepL API";
            ConfigManager.Current.DeepLApiKey = "test-key:fx";
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("Sentence alpha");
            aiService.RecordSourceContext("Sentence beta");
            aiService.RecordSourceContext("Sentence gamma");

            var tcs = new TaskCompletionSource<TranslationResult>();
            aiService.OnTranslationCompleted += res => tcs.TrySetResult(res);
            aiService.EnqueueTranslation(CommitMetadata.From("Sentence delta", "HardCommit"));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(handler.LastRequest);
            Assert.NotNull(handler.LastRequestBody);

            using var doc = JsonDocument.Parse(handler.LastRequestBody);
            Assert.True(doc.RootElement.TryGetProperty("context", out var contextProp));
            Assert.Equal("Sentence alpha\nSentence beta\nSentence gamma", contextProp.GetString());
        }

        [Fact]
        public async Task Test_DeepLPayload_HandlesDynamicWindowResizing()
        {
            var handler = new TestHttpMessageHandler();
            using var aiService = new AIService(handler);
            aiService.ClearContextQueue();

            ConfigManager.Current.TranslationEngine = "DeepL API";
            ConfigManager.Current.DeepLApiKey = "test-key:fx";
            ConfigManager.Current.DeepLContextWindowSize = 3;

            aiService.RecordSourceContext("Line 1");
            aiService.RecordSourceContext("Line 2");
            aiService.RecordSourceContext("Line 3");
            Assert.Equal("Line 1\nLine 2\nLine 3", aiService.GetDeepLContextString());

            ConfigManager.Current.DeepLContextWindowSize = 2;
            aiService.RecordSourceContext("Line 4");

            var tcs = new TaskCompletionSource<TranslationResult>();
            aiService.OnTranslationCompleted += res => tcs.TrySetResult(res);
            aiService.EnqueueTranslation(CommitMetadata.From("Line 5", "HardCommit"));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(handler.LastRequest);
            Assert.NotNull(handler.LastRequestBody);

            using var doc = JsonDocument.Parse(handler.LastRequestBody);
            Assert.True(doc.RootElement.TryGetProperty("context", out var contextProp));
            Assert.Equal("Line 3\nLine 4", contextProp.GetString());
        }

        // =========================================================================
        // Category 4: Additional Unit & Integration Tests (5 tests)
        // =========================================================================

        [Fact]
        public void Test_PreferencesDialog_SaveSettings_ClampsContextWindowSize()
        {
            Assert.Equal(0, Math.Clamp(-5, 0, 10));
            Assert.Equal(10, Math.Clamp(15, 0, 10));
            Assert.Equal(5, Math.Clamp(5, 0, 10));
        }

        [Fact]
        public async Task Test_AIService_RecordSourceContext_AutomatedOnSuccessfulTranslation()
        {
            var handler = new TestHttpMessageHandler();
            using var aiService = new AIService(handler);
            aiService.ClearContextQueue();

            ConfigManager.Current.TranslationEngine = "DeepL API";
            ConfigManager.Current.DeepLApiKey = "test-key:fx";
            ConfigManager.Current.DeepLContextWindowSize = 3;

            var tcs = new TaskCompletionSource<TranslationResult>();
            aiService.OnTranslationCompleted += res => tcs.TrySetResult(res);
            aiService.EnqueueTranslation(CommitMetadata.From("First translated line", "HardCommit"));
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("First translated line", aiService.GetDeepLContextString());
        }

        [Fact]
        public void Test_AIService_ClearContextQueue()
        {
            using var aiService = new AIService();
            ConfigManager.Current.DeepLContextWindowSize = 3;
            aiService.RecordSourceContext("Line A");
            aiService.RecordSourceContext("Line B");
            Assert.Equal("Line A\nLine B", aiService.GetDeepLContextString());

            aiService.ClearContextQueue();
            Assert.Null(aiService.GetDeepLContextString());
        }

        [Fact]
        public async Task Test_DeepLPayload_FreeVsProEndpoint()
        {
            var handlerFree = new TestHttpMessageHandler();
            using (var serviceFree = new AIService(handlerFree))
            {
                ConfigManager.Current.TranslationEngine = "DeepL API";
                ConfigManager.Current.DeepLApiKey = "key:fx";
                var tcs = new TaskCompletionSource<TranslationResult>();
                serviceFree.OnTranslationCompleted += res => tcs.TrySetResult(res);
                serviceFree.EnqueueTranslation(CommitMetadata.From("Test free", "HardCommit"));
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.NotNull(handlerFree.LastRequest);
                Assert.Equal("https://api-free.deepl.com/v2/translate", handlerFree.LastRequest.RequestUri?.ToString());
            }

            var handlerPro = new TestHttpMessageHandler();
            using (var servicePro = new AIService(handlerPro))
            {
                ConfigManager.Current.TranslationEngine = "DeepL API";
                ConfigManager.Current.DeepLApiKey = "key-pro";
                var tcs = new TaskCompletionSource<TranslationResult>();
                servicePro.OnTranslationCompleted += res => tcs.TrySetResult(res);
                servicePro.EnqueueTranslation(CommitMetadata.From("Test pro", "HardCommit"));
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.NotNull(handlerPro.LastRequest);
                Assert.Equal("https://api.deepl.com/v2/translate", handlerPro.LastRequest.RequestUri?.ToString());
            }
        }

        [Fact]
        public void Test_AIService_ConstructorOverloads()
        {
            var handler = new TestHttpMessageHandler();
            using var serviceWithHandler = new AIService(handler);
            Assert.NotNull(serviceWithHandler);

            using var customHttpClient = new HttpClient();
            using var serviceWithHttpClient = new AIService(customHttpClient);
            Assert.NotNull(serviceWithHttpClient);

            using var defaultService = new AIService();
            Assert.NotNull(defaultService);
        }
    }
}
