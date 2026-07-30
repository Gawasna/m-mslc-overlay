using System;
using System.Diagnostics;
using System.IO;
using Xunit;
using m_mslc_overlay.services;

namespace m_mslc_overlay.services.tests
{
    public class AppPathHelperTests
    {
        [Fact]
        public void Test_IsDevMode_LazyEvaluationAndPerformance()
        {
            // Measure time of accessing IsDevMode for the first time vs subsequent calls
            var sw = Stopwatch.StartNew();
            bool isDev = AppPathHelper.IsDevMode;
            sw.Stop();
            long firstAccessMs = sw.ElapsedMilliseconds;

            sw.Restart();
            for (int i = 0; i < 100000; i++)
            {
                bool mode = AppPathHelper.IsDevMode;
            }
            sw.Stop();
            long hundredThousandAccessesMs = sw.ElapsedMilliseconds;

            Assert.True(firstAccessMs < 50, $"First access took too long: {firstAccessMs}ms");
            Assert.True(hundredThousandAccessesMs < 20, $"100k accesses took too long: {hundredThousandAccessesMs}ms");
        }

        [Fact]
        public void Test_PathResolution_NoExcessiveDiskIO()
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                string configPath = AppPathHelper.GetConfigFilePath();
                string logsDir = AppPathHelper.GetLogsDirectory();
                string lockPath = AppPathHelper.GetLockFilePath();
                string pluginsDir = AppPathHelper.GetPluginsDirectory();
                string extractorDir = AppPathHelper.GetExtractorDirectory();
            }
            sw.Stop();
            long thousandIterationsMs = sw.ElapsedMilliseconds;

            // 1000 iterations resolving all 5 paths should execute in under 250ms
            Assert.True(thousandIterationsMs < 250, $"1000 path resolution iterations took too long: {thousandIterationsMs}ms");
        }

        [Fact]
        public void Test_GetWritablePath_RelativeAndRootedPaths()
        {
            string relative = AppPathHelper.GetWritablePath("test_sub/file.txt");
            Assert.Contains("test_sub", relative);

            string rooted = Path.GetFullPath("C:\\Windows\\System32");
            string resultRooted = AppPathHelper.GetWritablePath(rooted);
            Assert.Equal(rooted, resultRooted);
        }
    }
}
