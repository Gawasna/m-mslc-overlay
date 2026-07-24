using System;
using System.IO;
using MMslcOverlay.Core.Workspace.Storage;

namespace MMslcOverlay.Services.Workspace;

public class AudioRecorderService : IDisposable
{
    private readonly string _wavFilePath;
    private readonly AudioOffsetIndex _offsetIndex;
    private FileStream? _audioFileStream;
    private BinaryWriter? _audioWriter;
    private bool _isRecording;
    
    // Đây là khung sườn (skeleton) chuẩn bị cho tích hợp NAudio ở phase sau.

    public AudioRecorderService(string wavFilePath, AudioOffsetIndex offsetIndex)
    {
        _wavFilePath = wavFilePath;
        _offsetIndex = offsetIndex;
        InitializeWavFile();
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
    }
    
    public void StopRecording()
    {
        _isRecording = false;
        _audioWriter?.Flush();
    }

    /// <summary>
    /// Ghi PCM data vào file, đồng thời trả về offset hiện tại 
    /// để hệ thống gọi SyncSegmentOffset
    /// </summary>
    public long WriteAudioData(byte[] pcmData)
    {
        if (!_isRecording || _audioWriter == null || _audioFileStream == null)
            return -1;

        long currentOffset = _audioFileStream.Position;
        _audioWriter.Write(pcmData);
        return currentOffset;
    }

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
