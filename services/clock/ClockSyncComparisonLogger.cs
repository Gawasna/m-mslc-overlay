using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace m_mslc_overlay.services.clock
{
    public record ClockSyncLogEntry(
        double SystemTimestampMs,
        string EventType,
        bool IsFinal,
        ulong SdkOffsetTicks,
        double SdkOffsetMs,
        ulong SdkDurationTicks,
        double SdkDurationMs,
        bool AlgAIsAnchored,
        double AlgAAnchorMs,
        double AlgAPlayheadMs,
        bool AlgBIsAnchored,
        double AlgBDeltaPhaseMs,
        double AlgBPlayheadMs,
        double DiscrepancyMs,
        string Text
    );

    /// <summary>
    /// Non-blocking async channel logger writing clock-sync comparison metrics to logs/clock_sync_comparison.log.
    /// Uses System.Threading.Channels to ensure disk I/O does not block the main pipe receiving loop.
    /// </summary>
    public class ClockSyncComparisonLogger : IDisposable
    {
        private readonly string _logFilePath;
        private readonly Channel<ClockSyncLogEntry> _logChannel;
        private readonly CancellationTokenSource _cts;
        private readonly Task _processTask;

        public ClockSyncComparisonLogger(string? customLogPath = null)
        {
            _logFilePath = customLogPath ?? AppPathHelper.GetWritablePath("logs/clock_sync_comparison.log");

            var channelOptions = new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = true
            };
            _logChannel = Channel.CreateUnbounded<ClockSyncLogEntry>(channelOptions);
            _cts = new CancellationTokenSource();
            _processTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        }

        public void LogEvent(ClockSyncLogEntry entry)
        {
            _logChannel.Writer.TryWrite(entry);
        }

        private async Task ProcessQueueAsync(CancellationToken token)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, Encoding.UTF8);

                if (stream.Length == 0)
                {
                    await writer.WriteLineAsync(
                        "TimestampMs|EventType|IsFinal|OffsetMs|DurationMs|AlgA_Anchored|AlgA_AnchorMs|AlgA_PlayheadMs|AlgB_Anchored|AlgB_DeltaPhaseMs|AlgB_PlayheadMs|Diff_A_B_Ms|Text"
                    );
                    await writer.FlushAsync(token);
                }

                while (await _logChannel.Reader.WaitToReadAsync(token))
                {
                    while (_logChannel.Reader.TryRead(out var entry))
                    {
                        string sanitizedText = entry.Text
                            .Replace("\r", " ")
                            .Replace("\n", " ")
                            .Replace("|", "/");

                        string line = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:F2}|{1}|{2}|{3:F2}|{4:F2}|{5}|{6:F2}|{7:F2}|{8}|{9:F2}|{10:F2}|{11:F2}|{12}",
                            entry.SystemTimestampMs,
                            entry.EventType,
                            entry.IsFinal ? 1 : 0,
                            entry.SdkOffsetMs,
                            entry.SdkDurationMs,
                            entry.AlgAIsAnchored ? 1 : 0,
                            entry.AlgAAnchorMs,
                            entry.AlgAPlayheadMs,
                            entry.AlgBIsAnchored ? 1 : 0,
                            entry.AlgBDeltaPhaseMs,
                            entry.AlgBPlayheadMs,
                            entry.DiscrepancyMs,
                            sanitizedText
                        );

                        await writer.WriteLineAsync(line.AsMemory(), token);
                    }
                    await writer.FlushAsync(token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClockSyncComparisonLogger] Error writing log: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _logChannel.Writer.TryComplete();
            try
            {
                if (!_processTask.Wait(1000))
                {
                    _cts.Cancel();
                    _processTask.Wait(500);
                }
            }
            catch { }
            _cts.Dispose();
        }
    }
}
