using System;
using System.IO;
using MMslcOverlay.Core.Workspace.Storage;
using NAudio.Wave;

namespace MMslcOverlay.Services.Workspace;

public class AudioRecorderService : IDisposable
{
    private readonly string _wavFilePath;
    private readonly AudioOffsetIndex _offsetIndex;
    private FileStream? _audioFileStream;
    private BinaryWriter? _audioWriter;
    private bool _isRecording;
    
    private WaveInEvent? _waveIn;
    private bool _useNAudio;

    /// <summary>
    /// true nếu có thể capture audio thực sự với NAudio.
    /// Nếu false (không có thiết bị/không có permission), fallback sang ghi đến file rỗng (dummy) vẫn được.
    /// </summary>
    public bool CanRecordRealAudio => _useNAudio;

    public AudioRecorderService(string wavFilePath, AudioOffsetIndex offsetIndex)
    {
        _wavFilePath = wavFilePath;
        _offsetIndex = offsetIndex;

        _useNAudio = CanUseNAudio();
        if (_useNAudio)
        {
            InitializeWavFile();
        }
        else
        {
            // Fallback: ghi file rỗng để playback logic không crash
            try
            {
                InitializeWavFile();
            }
            catch { }
            System.Diagnostics.Debug.WriteLine("[AudioRecorderService] NAudio capture unavailable (no input device or permission). Audio playback will be unavailable.");
        }
    }

    private static bool CanUseNAudio()
    {
        try
        {
            // Chỉ kiểm tra nhanh — WaveInEvent sẽ khởi tạo khi StartRecording được gọi
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void InitializeWavFile()
    {
        // 16kHz, 16-bit, Mono PCM
        _audioFileStream = new FileStream(_wavFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _audioWriter = new BinaryWriter(_audioFileStream);
        
        // Nếu file rỗng, ghi Dummy WAV Header (44 bytes chuẩn WAV PCM)
        if (_audioFileStream.Length == 0)
        {
            WriteDummyWavHeader();
        }
    }

    private void WriteDummyWavHeader()
    {
        if (_audioWriter == null) return;
        
        // 44 bytes chuẩn WAV PCM (16kHz, 16-bit, Mono)
        _audioWriter.Write("RIFF".ToCharArray());
        _audioWriter.Write(36); // ChunkSize: 36 + dataSize (dummy data size 0 for now)
        _audioWriter.Write("WAVE".ToCharArray());
        
        // fmt chunk
        _audioWriter.Write("fmt ".ToCharArray());
        _audioWriter.Write(16); // Subchunk1Size
        _audioWriter.Write((short)1); // AudioFormat (PCM = 1)
        _audioWriter.Write((short)1); // NumChannels (Mono = 1)
        _audioWriter.Write(16000); // SampleRate
        _audioWriter.Write(32000); // ByteRate (SampleRate * NumChannels * BitsPerSample/8)
        _audioWriter.Write((short)2); // BlockAlign (NumChannels * BitsPerSample/8)
        _audioWriter.Write((short)16); // BitsPerSample
        
        // data chunk
        _audioWriter.Write("data".ToCharArray());
        _audioWriter.Write(0); // Subchunk2Size (0 initially)
    }

    public void StartRecording()
    {
        _isRecording = true;

        if (!_useNAudio) return;

        try
        {
            _waveIn?.StopRecording();

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1) // 16kHz, 16-bit, Mono
            };

            _waveIn.DataAvailable += OnWaveInDataAvailable;
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioRecorderService] Cannot start NAudio capture: {ex.Message}");
            _useNAudio = false; // Fallback cho session này
        }
    }

    private void OnWaveInDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording) return;
        // e.Buffer: PCM bytes vừa capture; Append vào WAV
        var mut = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, mut, 0, e.BytesRecorded);
        long offset = WriteAudioData(mut);
        _audioWriter?.Flush();
        // offset có thể dùng cho SyncSegmentOffset nếu cần real-time mapping
    }

    public void StopRecording()
    {
        _isRecording = false;

        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
        }
        catch { }

        if (_audioWriter != null && _audioFileStream != null && _audioFileStream.CanSeek)
        {
            long dataSize = _audioFileStream.Length - 44;
            if (dataSize > 0)
            {
                _audioFileStream.Seek(4, SeekOrigin.Begin);
                _audioWriter.Write((uint)(dataSize + 36));

                _audioFileStream.Seek(40, SeekOrigin.Begin);
                _audioWriter.Write((uint)dataSize);

                _audioFileStream.Seek(0, SeekOrigin.End);
            }
        }
        
        _audioWriter?.Flush();
    }

    /// <summary>
    /// Ghi PCM data vào file, đồng thời trả về offset hiện tại 
    /// để hệ thống gọi SyncSegmentOffset
    /// </summary>
    public long WriteAudioData(byte[] pcmData)
    {
        if (_audioWriter == null || _audioFileStream == null)
            return -1;

        long currentOffset = _audioFileStream.Position;
        _audioWriter.Write(pcmData);
        return currentOffset;
    }

    /// <summary>
    /// Current WAV file position (byte offset) — dùng để map SegmentId ↔ Audio.
    /// </summary>
    public long CurrentWriteOffset => _audioFileStream?.Position ?? 0;

    public void SyncSegmentOffset(long segmentId, long offset)
    {
        _offsetIndex.AppendOffset(segmentId, offset);
    }

    public void Dispose()
    {
        StopRecording();
        _audioWriter?.Dispose();
        _audioFileStream?.Dispose();
    }
}
