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
        var header = new byte[44];
        _audioWriter?.Write(header);
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
