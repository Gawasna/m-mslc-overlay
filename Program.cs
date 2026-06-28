using System;
using Avalonia;

namespace m_mslc_overlay
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--test-extractor-update")
            {
                RunTestUpdate().GetAwaiter().GetResult();
                return;
            }

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }

        private static async System.Threading.Tasks.Task RunTestUpdate()
        {
            Console.WriteLine("=== EXTRACTOR AUTO-UPDATE TEST MODE ===");
            
            services.ExtractorUpdateService.OnLogReceived += msg => Console.WriteLine(msg);
            services.ExtractorUpdateService.OnProgressChanged += val => Console.WriteLine($"[PROGRESS] {val:0.0}%");
            
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            services.ExtractorUpdateService.OnUpdateCompleted += (success, msg) =>
            {
                if (success)
                {
                    Console.WriteLine($"[SUCCESS] {msg}");
                    tcs.SetResult(true);
                }
                else
                {
                    Console.WriteLine($"[FAILED] {msg}");
                    tcs.SetResult(false);
                }
            };

            Console.WriteLine("Checking for update...");
            var release = await services.ExtractorUpdateService.CheckForUpdateAsync();
            if (release == null)
            {
                Console.WriteLine("No release found or error checking update.");
                Environment.Exit(1);
            }

            Console.WriteLine($"Latest release found: {release.TagName}");
            
            services.ConfigManager.Load();
            services.ConfigManager.Current.ExtractorTag = "";
            services.ConfigManager.Save();

            Console.WriteLine("Starting update...");
            await services.ExtractorUpdateService.RunUpdateAsync(release);
            
            bool success = await tcs.Task;
            if (success)
            {
                string baseDir = AppContext.BaseDirectory;
                string hostPath = System.IO.Path.Combine(baseDir, "extractor", "Host.exe");
                string agentPath = System.IO.Path.Combine(baseDir, "extractor", "Agent.dll");
                
                if (System.IO.File.Exists(hostPath) && System.IO.File.Exists(agentPath))
                {
                    Console.WriteLine("Files mapped correctly!");
                    Environment.Exit(0);
                }
                else
                {
                    Console.WriteLine("Error: Files not found at expected location after update.");
                    Environment.Exit(1);
                }
            }
            else
            {
                Environment.Exit(1);
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
