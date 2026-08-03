using System;
using System.Threading.Tasks;
using Xunit;
using m_mslc_overlay.services;
using m_mslc_overlay.core;

namespace m_mslc_overlay.services.tests
{
    public class AIServiceIntegrationTests
    {
        [Fact]
        public async Task Test_DeepLAPI_RealConnection_WithContext()
        {
            // Load configuration
            ConfigManager.Load();
            string apiKey = ConfigManager.Current.DeepLApiKey;

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
            {
                // Skip the test if key is empty or placeholder
                Assert.True(true, "Skipped: DeepL API Key is not configured.");
                return;
            }

            // Temporarily set configuration for integration test
            string originalEngine = ConfigManager.Current.TranslationEngine;
            int originalSize = ConfigManager.Current.DeepLContextWindowSize;
            
            try
            {
                ConfigManager.Current.TranslationEngine = "DeepL API";
                ConfigManager.Current.DeepLContextWindowSize = 3;

                using var aiService = new AIService();
                aiService.ClearContextQueue();

                // Record some mock speech context
                aiService.RecordSourceContext("The primary character in the story is a young girl.");
                aiService.RecordSourceContext("She loves reading books under the oak tree.");

                // Sentence to translate containing pronouns referencing the context
                string textToTranslate = "Her mother calls her for dinner.";

                var tcs = new TaskCompletionSource<TranslationResult>();
                aiService.OnTranslationCompleted += res =>
                {
                    tcs.TrySetResult(res);
                };

                // Enqueue translation
                aiService.EnqueueTranslation(CommitMetadata.From(textToTranslate, "HardCommit"));

                // Wait for the translation to complete (max 10 seconds)
                var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // Verify translation output
                Assert.NotNull(result);
                Assert.False(result.IsError, $"Translation failed with error: {result.Translation}");
                Assert.NotEmpty(result.Translation);
                
                // Assert that the result was recorded to context queue
                // The context queue should contain the sentence we just translated
                Assert.Contains(textToTranslate, aiService.GetDeepLContextString() ?? "");
            }
            finally
            {
                // Restore original config
                ConfigManager.Current.TranslationEngine = originalEngine;
                ConfigManager.Current.DeepLContextWindowSize = originalSize;
            }
        }

        [Fact]
        public async Task Test_DeepLAPI_RealParagraph_ContextAwareFlow()
        {
            // Load configuration
            ConfigManager.Load();
            string apiKey = ConfigManager.Current.DeepLApiKey;

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
            {
                Assert.True(true, "Skipped: DeepL API Key is not configured.");
                return;
            }

            string originalEngine = ConfigManager.Current.TranslationEngine;
            int originalSize = ConfigManager.Current.DeepLContextWindowSize;
            
            try
            {
                ConfigManager.Current.TranslationEngine = "DeepL API";
                ConfigManager.Current.DeepLContextWindowSize = 3; // Keep last 3 sentences as context

                using var aiService = new AIService();
                aiService.ClearContextQueue();

                // Paragraph split into consecutive sentences
                string[] sentences = new[]
                {
                    "Yesterday, I went to a local shelter and adopted a cute puppy.",
                    "It was very small and energetic.",
                    "I decided to name him Max.",
                    "He immediately fell asleep in my arms."
                };

                Console.WriteLine("\n=== [Real Paragraph Translation Test] ===");
                
                for (int i = 0; i < sentences.Length; i++)
                {
                    string currentSentence = sentences[i];
                    string currentContext = aiService.GetDeepLContextString() ?? "[No Context]";
                    
                    var tcs = new TaskCompletionSource<TranslationResult>();
                    Action<TranslationResult> handler = null!;
                    handler = res =>
                    {
                        aiService.OnTranslationCompleted -= handler;
                        tcs.TrySetResult(res);
                    };
                    aiService.OnTranslationCompleted += handler;

                    aiService.EnqueueTranslation(CommitMetadata.From(currentSentence, "HardCommit"));
                    
                    var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    
                    Assert.NotNull(result);
                    Assert.False(result.IsError, $"Failed at sentence {i}: {result.Translation}");
                    
                    Console.WriteLine($"\nSentence #{i + 1}: {currentSentence}");
                    Console.WriteLine($"Context passed: {currentContext.Replace("\n", " | ")}");
                    Console.WriteLine($"Translation: {result.Translation}");

                    // Record the source sentence as context for the NEXT translations
                    aiService.RecordSourceContext(currentSentence);
                }
                
                Console.WriteLine("=========================================\n");
            }
            finally
            {
                ConfigManager.Current.TranslationEngine = originalEngine;
                ConfigManager.Current.DeepLContextWindowSize = originalSize;
            }
        }

        [Fact]
        public async Task Test_DeepLAPI_HeavyMeetingParagraph_ContextFlow()
        {
            // Load configuration
            ConfigManager.Load();
            string apiKey = ConfigManager.Current.DeepLApiKey;

            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("YOUR_"))
            {
                Assert.True(true, "Skipped: DeepL API Key is not configured.");
                return;
            }

            string originalEngine = ConfigManager.Current.TranslationEngine;
            int originalSize = ConfigManager.Current.DeepLContextWindowSize;
            
            try
            {
                ConfigManager.Current.TranslationEngine = "DeepL API";
                ConfigManager.Current.DeepLContextWindowSize = 3; // Context size = 3

                using var aiService = new AIService();
                aiService.ClearContextQueue();

                string[] sentences = new[]
                {
                    "Alright everyone, thanks for hopping on this call at the eleventh hour, especially since I know half of you are already running on fumes.",
                    "Let’s address the elephant in the room right away: the legacy migration we pushed to production last night is currently cascading failures across the client’s main dashboard.",
                    "Alex, since you have been fielding their frantic calls all morning, can you walk us through the specific blowback we're seeing on their end?",
                    "Sure thing, Sarah, and to put it mildly, their executive board is absolutely livid right now.",
                    "The primary issue they are reporting is that whenever their accounting department tries to pull the quarterly revenue aggregates, the interface just spins indefinitely before timing out and throwing a generic 502 Bad Gateway error.",
                    "Their CFO is personally threatening to pull the plug on the entire second phase of the contract if we do not have a hotfix deployed by end of day, claiming this constitutes a material breach of our Service Level Agreement.",
                    "David, from a backend engineering perspective, how on earth did this slip through our quality assurance processes when it was running flawlessly in the staging environment yesterday?",
                    "Well, that is the kicker, Sarah; it isn't actually an infrastructure bottleneck or a memory leak on our end, despite what their telemetry dashboard suggests.",
                    "After digging deep into the server logs this morning, my team realized that the client quietly updated their own database schema over the weekend without giving us any prior heads-up.",
                    "They appended a bunch of nested, non-standard JSON objects into the user profiles, which is completely choking our parsing logic because we were explicitly told to expect strict alphanumeric values.",
                    "Basically, they are feeding our data pipeline pure garbage, and since our system isn't designed to sanitize fields that were contractually guaranteed to be clean, it's getting caught in an infinite retry loop.",
                    "Wait a minute, if they altered the data structure unilaterally, why are they throwing us under the bus instead of taking accountability for their own oversight?",
                    "Because, Alex, it's always easier to blame the external vendor, and frankly, their project manager probably didn't even realize the downstream impact of their internal patch.",
                    "If we try to push back aggressively and quote the API documentation right now, they will just dig their heels in, which is a political nightmare we cannot afford right before the contract renewal discussions.",
                    "So, David, instead of pointing fingers and playing the blame game, what is the quickest workaround to stop the bleeding while we figure out a long-term architectural refactor?",
                    "I can have the senior engineers write a middleware script to aggressively strip out those unrecognized characters before the payload even hits our core database.",
                    "It is a very hacky band-aid, and I hate accruing this much technical debt just to cover for their negligence, but it will stabilize the user interface and stop the server timeouts.",
                    "The catch is that dropping those nested objects means their generated reports might miss some auxiliary data, which could cause minor discrepancies in their final financial ledgers.",
                    "I think we just have to bite the bullet on that one; a slightly inaccurate report is vastly better than a totally bricked application.",
                    "Alex, I need you to draft a very diplomatic update to their stakeholders immediately.",
                    "Frame it as an 'unexpected edge-case optimization' on our part, reassuring them that the servers are stabilizing, but absolutely do not admit any fault.",
                    "Just read between the lines and let them know we are actively calibrating the data parsers to accommodate their 'latest system upgrades' so they can connect the dots themselves without losing face.",
                    "I can certainly do that, but what happens if they push for a formal root-cause analysis document by tomorrow morning?",
                    "We will cross that bridge when we come to it; right now, David, I need all hands on deck to deploy that middleware patch before the East Coast logs off for the day.",
                    "I'll pull two guys off the mobile app sprint to help write the unit tests, but it is going to be a tight squeeze given the time frame.",
                    "Understood, just keep the main Slack channel updated with your deployment status, and ping me the minute the build passes so I can give Alex the green light to send the email.",
                    "Let’s get this sorted, team, keep your heads down, and hopefully we can salvage the rest of the week."
                };

                Console.WriteLine("\n=== [Real Heavy Paragraph Translation Test] ===");
                
                for (int i = 0; i < sentences.Length; i++)
                {
                    string currentSentence = sentences[i];
                    string currentContext = aiService.GetDeepLContextString() ?? "[No Context]";
                    
                    var tcs = new TaskCompletionSource<TranslationResult>();
                    Action<TranslationResult> handler = null!;
                    handler = res =>
                    {
                        aiService.OnTranslationCompleted -= handler;
                        tcs.TrySetResult(res);
                    };
                    aiService.OnTranslationCompleted += handler;

                    aiService.EnqueueTranslation(CommitMetadata.From(currentSentence, "HardCommit"));
                    
                    var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    
                    Assert.NotNull(result);
                    Assert.False(result.IsError, $"Failed at sentence {i}: {result.Translation}");
                    
                    Console.WriteLine($"\n[Sentence #{i + 1}] Original: {currentSentence}");
                    Console.WriteLine($"[Context Passed]: {currentContext.Replace("\n", " | ")}");
                    Console.WriteLine($"[Translation]: {result.Translation}");

                    // Record the source sentence as context for the NEXT translations
                    aiService.RecordSourceContext(currentSentence);
                }
                
                Console.WriteLine("=========================================\n");
            }
            finally
            {
                ConfigManager.Current.TranslationEngine = originalEngine;
                ConfigManager.Current.DeepLContextWindowSize = originalSize;
            }
        }
    }
}
