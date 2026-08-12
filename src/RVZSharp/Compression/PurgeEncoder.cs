using System.Security.Cryptography;

namespace RVZSharp.Compression;

/// <summary>
/// The WIA-only PURGE encoder (mirrors Dolphin's PurgeCompressor): non-zero runs are stored
/// as (u32 BE offset, u32 BE length) descriptors followed by the run's bytes; runs of more
/// than 8 zeroes are skipped. The SHA-1 trailer covers the segments plus the exception lists
/// that precede the stream in the group.
/// </summary>
public sealed class PurgeEncoder : ICompressionEncoder
{
    private const int SegmentSize = 8;

    private readonly List<byte[]> _preceding = [];

    public void AddPrecedingData(ReadOnlySpan<byte> data) => _preceding.Add(data.ToArray());

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var segments = new MemoryStream();
        var bytesRead = 0;
        while (true)
        {
            var firstNonZero = bytesRead;
            while (firstNonZero < data.Length && data[firstNonZero] == 0)
            {
                firstNonZero++;
            }

            if (firstNonZero == data.Length)
            {
                break;
            }

            var nonZeroEnd = firstNonZero;
            var sequenceLength = 0;
            for (var i = firstNonZero; i < data.Length; i++)
            {
                if (data[i] == 0)
                {
                    if (++sequenceLength > SegmentSize)
                    {
                        break;
                    }
                }
                else
                {
                    sequenceLength = 0;
                    nonZeroEnd = i + 1;
                }
            }

            var length = nonZeroEnd - firstNonZero;
            WriteBe32(segments, (uint)firstNonZero);
            WriteBe32(segments, (uint)length);
            segments.Write(data.Slice(firstNonZero, length));
            bytesRead = nonZeroEnd;
        }

        var segmentBytes = segments.ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var preceding in _preceding)
        {
            hash.AppendData(preceding);
        }

        _preceding.Clear(); // each group's stream is hashed with its own preceding lists
        hash.AppendData(segmentBytes);
        var trailer = hash.GetHashAndReset();

        var output = new byte[segmentBytes.Length + trailer.Length];
        segmentBytes.CopyTo(output, 0);
        trailer.CopyTo(output, segmentBytes.Length);
        return output;
    }

    private static void WriteBe32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
