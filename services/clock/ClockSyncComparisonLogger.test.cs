using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using m_mslc_overlay.services.clock;

namespace m_mslc_overlay.services.clock.tests
{
    public class ClockSyncComparisonLoggerTests
    {
        [Fact]
        public void Test_Dispose_DrainsQueuedLogEntriesBeforeCancellation()
        {
            string tempLogFile = Path.Combine(Path.GetTempPath(), $"clock_test_{Guid.NewGuid():N}.log");
            try
            {
                using (var logger = new ClockSyncComparisonLogger(tempLogFile))
                {
                    logger.LogEvent(new ClockSyncLogEntry(
                        SystemTimestampMs: 1000.0,
                        EventType: "TEST_EVENT",
                        IsFinal: true,
                        SdkOffsetTicks: 1000000,
                        SdkOffsetMs: 100.0,
                        SdkDurationTicks: 500000,
                        SdkDurationMs: 50.0,
                        AlgAIsAnchored: true,
                        AlgAAnchorMs: 950.0,
                        AlgAPlayheadMs: 150.0,
                        AlgBIsAnchored: true,
                        AlgBDeltaPhaseMs: -50.0,
                        AlgBPlayheadMs: 150.0,
                        DiscrepancyMs: 0.0,
                        Text: "Test log entry"
                    ));
                } // Dispose called here

                Assert.True(File.Exists(tempLogFile), "Log file should be created");
                string content = File.ReadAllText(tempLogFile);
                Assert.Contains("TimestampMs|EventType", content);
                Assert.Contains("TEST_EVENT", content);
                Assert.Contains("Test log entry", content);
            }
            finally
            {
                if (File.Exists(tempLogFile))
                {
                    try { File.Delete(tempLogFile); } catch { }
                }
            }
        }
    }
}
