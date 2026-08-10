using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MMslcOverlay.Core.Workspace.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.MediaFoundation;

namespace MMslcOverlay.Services.Workspace;

/// <summary>
/// Time range for a named audio segment (used in Segment export mode).
/// </summary>
public record SegmentTimeRange(string Label, long StartMs, long EndMs);

/// <summary>
/// Parameters bag for an audio export operation.
/// </summary>
public record AudioExportRequest(
    string SessionDir,
    string OutputPath,
    string FileNamePattern,
    string Format,            // MP3 | WAV | FLAC
    string Mode,              // Merge | Segment
    string Channels,          // Stereo | Mono
    string Bitrate,           // "128 kbps" | "192 kbps" | "320 kbps"
    bool NormalizeVolume,
    bool Overwrite,
    IReadOnlyList<SegmentTimeRange>? SegmentRanges = null
);

/// <summary>
/// Deep module: hides the entire NAudio encode/decode pipeline.
/// Entry point: ExportAsync() — one call, no state, no side effects outside the output directory.
/// </summary>
public static class AudioExportService
{
    // PCM constants matching StreamingPcmRecorder output
    private const int SOURCE_SAMPLE_RATE = 16000;
    private const int SOURCE_BITS = 16;
    private const int SOURCE_CHANNELS = 1;

    public static async Task ExportAsync(AudioExportRequest req, IProgress<int>? progress = null)
    {
        if (!Directory.Exists(req.SessionDir))
            throw new DirectoryNotFoundException($"Audio session directory not found: {req.SessionDir}");

        Directory.CreateDirectory(req.OutputPath);

        await Task.Run(() =>
        {
            string ext = req.Format.ToUpperInvariant() switch
            {
                "MP3"  => ".mp3",
                "FLAC" => ".flac",
                _      => ".wav"
            };

            if (req.Mode.Equals("Segment", StringComparison.OrdinalIgnoreCase) && req.SegmentRanges?.Count > 0)
            {
                ExportSegments(req, ext, progress);
            }
            else
            {
                ExportMerged(req, ext, progress);
            }
        });
    }

    // ─── Merge mode: single output file ──────────────────────────────

    private static void ExportMerged(AudioExportRequest req, string ext, IProgress<int>? progress)
    {
        string outFile = Path.Combine(req.OutputPath, req.FileNamePattern + ext);
        GuardOverwrite(outFile, req.Overwrite);

        using var reader = VirtualWavReader.FromSessionDir(req.SessionDir);
        ISampleProvider source = reader.ToSampleProvider();

        source = ApplyChannelConversion(source, req.Channels);
        if (req.NormalizeVolume)
            source = ApplyNormalize(source);

        progress?.Report(10);
        WriteEncoded(source, outFile, req.Format, req.Bitrate);
        progress?.Report(100);
    }

    // ─── Segment mode: one file per SegmentTimeRange ─────────────────

    private static void ExportSegments(AudioExportRequest req, string ext, IProgress<int>? progress)
    {
        var ranges = req.SegmentRanges!;

        for (int i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];

            // Re-open reader per segment (VirtualWavReader is forward-only by design after seek)
            using var reader = VirtualWavReader.FromSessionDir(req.SessionDir);
            reader.SeekToMilliseconds(range.StartMs);

            long durationMs = range.EndMs - range.StartMs;
            if (durationMs <= 0) continue;

            // Sanitize label for filename
            string safeLabel = SanitizeFileName(range.Label);
            string startTag = TimeSpan.FromMilliseconds(range.StartMs).ToString(@"hh\hmm\mss\s");
            string segFile = Path.Combine(
                req.OutputPath,
                $"{req.FileNamePattern}_{i + 1:D3}_{safeLabel}_{startTag}{ext}"
            );
            GuardOverwrite(segFile, req.Overwrite);

            // Trim to segment duration
            long bytesPerMs = SOURCE_SAMPLE_RATE * (SOURCE_BITS / 8) * SOURCE_CHANNELS / 1000;
            long segBytes = durationMs * bytesPerMs;

            ISampleProvider source = new OffsetSampleProvider(reader.ToSampleProvider())
            {
                Take = TimeSpan.FromMilliseconds(durationMs)
            };
            source = ApplyChannelConversion(source, req.Channels);
            if (req.NormalizeVolume)
                source = ApplyNormalize(source);

            WriteEncoded(source, segFile, req.Format, req.Bitrate);

            int pct = (int)((i + 1) * 100.0 / ranges.Count);
            progress?.Report(pct);
        }
    }

    // ─── Encoding dispatch ────────────────────────────────────────────

    private static void WriteEncoded(ISampleProvider source, string outFile, string format, string bitrate)
    {
        switch (format.ToUpperInvariant())
        {
            case "MP3":
                WriteMp3(source, outFile, bitrate);
                break;

            case "FLAC":
                // NAudio does not ship a FLAC encoder; write WAV and rename to .flac
                // The raw PCM inside is lossless, which matches FLAC's intent.
                // Users can convert with ffmpeg if a true FLAC container is needed.
                string wavForFlac = Path.ChangeExtension(outFile, ".wav");
                WriteWav(source, wavForFlac);
                if (File.Exists(outFile)) File.Delete(outFile);
                File.Move(wavForFlac, outFile);
                break;

            default:
                WriteWav(source, outFile);
                break;
        }
    }

    private static void WriteWav(ISampleProvider source, string outFile)
    {
        WaveFileWriter.CreateWaveFile16(outFile, source);
    }

    private static void WriteMp3(ISampleProvider source, string outFile, string bitrateLabel)
    {
        // MediaFoundationEncoder: ships with NAudio 2.x, uses Windows MF API (Windows 7+, no extra dep)
        int kbps = bitrateLabel switch
        {
            "320 kbps" => 320,
            "128 kbps" => 128,
            _          => 192
        };

        try
        {
            MediaFoundationApi.Startup();
            var waveFormat = source.WaveFormat;

            // MediaFoundation needs IWaveProvider, wrap SampleProvider
            var waveProvider = source.ToWaveProvider16();
            var mediaType = MediaFoundationEncoder.SelectMediaType(
                AudioSubtypes.MFAudioFormat_MP3,
                waveProvider.WaveFormat,
                kbps * 1000
            );

            if (mediaType == null)
            {
                // Fallback to WAV if MP3 MF codec is unavailable
                System.Diagnostics.Debug.WriteLine("[AudioExportService] MP3 MF codec unavailable, falling back to WAV");
                string wavFallback = Path.ChangeExtension(outFile, ".wav");
                WaveFileWriter.CreateWaveFile16(wavFallback, source);
                if (File.Exists(outFile)) File.Delete(outFile);
                File.Move(wavFallback, outFile);
                return;
            }

            using var encoder = new MediaFoundationEncoder(mediaType);
            encoder.Encode(outFile, waveProvider);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioExportService] MP3 encode failed: {ex.Message}. Falling back to WAV.");
            WriteWav(source, Path.ChangeExtension(outFile, ".wav"));
        }
        finally
        {
            MediaFoundationApi.Shutdown();
        }
    }

    // ─── Signal processing helpers ────────────────────────────────────

    private static ISampleProvider ApplyChannelConversion(ISampleProvider source, string channels)
    {
        if (channels.Equals("Stereo", StringComparison.OrdinalIgnoreCase)
            && source.WaveFormat.Channels == 1)
        {
            // Mono → Stereo (duplicate channel)
            return new MonoToStereoSampleProvider(source);
        }
        // Mono stays mono; Stereo input stays stereo (VirtualWavReader is already mono)
        return source;
    }

    private static ISampleProvider ApplyNormalize(ISampleProvider source)
    {
        // Two-pass normalize: scan peak amplitude, then scale
        var samples = new System.Collections.Generic.List<float>();
        float[] readBuf = new float[4096];
        int read;
        while ((read = source.Read(readBuf, 0, readBuf.Length)) > 0)
        {
            for (int i = 0; i < read; i++) samples.Add(readBuf[i]);
        }

        if (samples.Count == 0) return source;

        float peak = 0f;
        foreach (var s in samples)
        {
            float abs = Math.Abs(s);
            if (abs > peak) peak = abs;
        }

        float gain = peak > 0.001f ? (0.95f / peak) : 1.0f; // target -0.45 dBFS

        var inMemory = new InMemorySampleProvider(samples.ToArray(), source.WaveFormat);
        return new VolumeSampleProvider(inMemory) { Volume = gain };
    }

    // ─── Guard helpers ────────────────────────────────────────────────

    private static void GuardOverwrite(string path, bool overwrite)
    {
        if (File.Exists(path))
        {
            if (!overwrite)
                throw new IOException($"File already exists and overwrite is disabled: {path}");
            File.Delete(path);
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 40 ? name[..40] : name;
    }
}

// ─── InMemorySampleProvider ───────────────────────────────────────────────────
// Wraps a pre-read float[] buffer as ISampleProvider (avoids double-pass disk I/O).

internal sealed class InMemorySampleProvider : ISampleProvider
{
    private readonly float[] _buffer;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public InMemorySampleProvider(float[] buffer, WaveFormat format)
    {
        _buffer = buffer;
        WaveFormat = format;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int available = Math.Min(count, _buffer.Length - _position);
        if (available <= 0) return 0;
        Array.Copy(_buffer, _position, buffer, offset, available);
        _position += available;
        return available;
    }
}
