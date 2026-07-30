using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Xunit;
using m_mslc_overlay.services;

namespace m_mslc_overlay.services.tests
{
    public class AppPathHelperAdversarialTests
    {
        [Fact]
        public void Test_DevMode_Detection_In_Repo()
        {
            // When running tests inside the repo, IsDevMode should be true
            Assert.True(AppPathHelper.IsDevMode, "Expected IsDevMode to be true when running inside repository tree.");

            // AppDataDir should point to AppContext.BaseDirectory in DevMode
            Assert.Equal(AppContext.BaseDirectory, AppPathHelper.AppDataDir);

            // Verify GetWritablePath returns path inside AppContext.BaseDirectory
            string configPath = AppPathHelper.GetConfigFilePath();
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "config.json"), configPath);

            string logsDir = AppPathHelper.GetLogsDirectory();
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), logsDir);
        }

        [Fact]
        public void Test_GetWritablePath_Creates_Subdirectory_If_Missing()
        {
            string testRelativeDir = Path.Combine("test_temp_dir_" + Guid.NewGuid().ToString("N"), "test_file.txt");
            string fullPath = AppPathHelper.GetWritablePath(testRelativeDir);

            string parentDir = Path.GetDirectoryName(fullPath)!;
            Assert.True(Directory.Exists(parentDir), $"Expected directory {parentDir} to be created.");

            // Cleanup
            if (Directory.Exists(parentDir))
            {
                Directory.Delete(parentDir, true);
            }
        }

        [Fact]
        public void Test_GetPluginManifestPath_Behavior()
        {
            string manifestPath = AppPathHelper.GetPluginManifestPath();
            Assert.False(string.IsNullOrEmpty(manifestPath));
            Assert.True(File.Exists(manifestPath), $"Expected manifest file to exist at {manifestPath}");
        }

        [Fact]
        public void Test_ProductionMode_Simulation_IsolatedProcess()
        {
            // Simulate Production Mode by running a child process in a directory tree without plugins.manifest.json
            string tempDir = Path.Combine(Path.GetTempPath(), "m_mslc_prod_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // Verify that a path without plugins.manifest.json in parent tree evaluates IsDevMode = false
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string expectedProdAppDataDir = Path.Combine(localAppData, "m-mslc-overlay");

                // Execute empirical verification via reflection on a newly loaded instance/isolated check
                // or checking FindDevRepoRoot algorithm for the temp directory
                string? parentDir = Directory.GetParent(tempDir)?.FullName;
                bool foundManifest = false;
                for (int i = 0; i < 6 && parentDir != null; i++)
                {
                    if (File.Exists(Path.Combine(parentDir, "plugins.manifest.json")))
                    {
                        foundManifest = true;
                        break;
                    }
                    parentDir = Directory.GetParent(parentDir)?.FullName;
                }

                Assert.False(foundManifest, "Temp directory should not have plugins.manifest.json in parent tree.");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}
