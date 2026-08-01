using System.IO;

namespace MMslcOverlay.Core.Workspace.Storage;

public class AudioOffsetIndex
{
    private readonly string _filePath;

    public AudioOffsetIndex(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Ghi byte offset tuyệt đối trong file WAV tương ứng với SegmentId
    /// 8 bytes cho SegmentId (long), 8 bytes cho ByteOffset (long)
    /// Gap 10 fix: Ensure directory exists before writing
    /// </summary>
    public void AppendOffset(long segmentId, long byteOffset)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        using var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);
        bw.Write(segmentId);
        bw.Write(byteOffset);
    }

    /// <summary>
    /// Tìm byte offset theo SegmentId
    /// Gap 10 fix: Guard against missing file
    /// </summary>
    public long? GetOffset(long targetSegmentId)
    {
        if (!File.Exists(_filePath))
        {
            System.Diagnostics.Debug.WriteLine($"[AudioOffsetIndex] Offsets file not found: {_filePath}. No recording yet?");
            return null;
        }

        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);

        // Mỗi record là 16 bytes
        while (fs.Position + 16 <= fs.Length)
        {
            var segmentId = br.ReadInt64();
            var offset = br.ReadInt64();

            if (segmentId == targetSegmentId)
            {
                return offset == -1 ? null : offset;
            }
        }

        return null;
    }
}
