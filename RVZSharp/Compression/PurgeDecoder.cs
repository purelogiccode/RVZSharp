using System.Security.Cryptography;

namespace RVZSharp.Compression;

/// <summary>
/// The WIA-only PURGE codec (Dolphin: PurgeDecompressor). The stored stream is a sequence of
/// segment descriptors (u32 BE offset + u32 BE size) followed by the segment bytes themselves,
/// terminated by a 20-byte SHA-1 of everything before it (exception lists + padding included).
/// The decompressed output is the segment bytes placed at their offsets, zero-filled between
/// segments and up to the expected size. Only used by WIA files.
/// </summary>
public static class PurgeDecoder
{
    /// <summary>Length in bytes of the SHA-1 trailer appended to PURGE streams.</summary>
    public const int TrailerSize = 20;

    private const int SegmentSize = 8;

    /// <summary>
    /// Decodes a PURGE-compressed buffer into exactly <paramref name="outputSize"/> bytes.
    /// </summary>
    /// <param name="stream">The stored bytes after the exception lists (segments + SHA-1 trailer).</param>
    /// <param name="precedingData">The exception lists (and NONE/PURGE padding) that precede the
    /// stream in the group; included in the trailer hash. Empty for table chunks.</param>
    /// <param name="outputSize">Expected decompressed size.</param>
    /// <exception cref="RvzFormatException">The stream is truncated or the segments are out of bounds.</exception>
    /// <exception cref="RvzHashMismatchException">The SHA-1 trailer does not match.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> stream, ReadOnlySpan<byte> precedingData, int outputSize)
    {
        if (stream.Length < TrailerSize)
        {
            throw new RvzFormatException(
                $"PURGE stream is too short: {stream.Length} bytes, need at least {TrailerSize}.");
        }

        var streamEnd = stream.Length - TrailerSize;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(precedingData);
        hash.AppendData(stream[..streamEnd]);
        var actualHash = hash.GetHashAndReset();
        if (!actualHash.AsSpan().SequenceEqual(stream[streamEnd..]))
        {
            throw new RvzHashMismatchException("The PURGE SHA-1 trailer does not match its contents.");
        }

        var output = new byte[outputSize];
        var inputPos = 0;
        var outputPos = 0;
        while (inputPos < streamEnd)
        {
            if (streamEnd - inputPos < SegmentSize)
            {
                throw new RvzFormatException(
                    $"PURGE stream ends with a truncated segment descriptor at byte {inputPos}.");
            }

            var offset = ReadBe32(stream, inputPos);
            var size = ReadBe32(stream, inputPos + 4);
            inputPos += SegmentSize;

            if (offset > outputSize || (long)offset + size > outputSize)
            {
                throw new RvzFormatException(
                    $"PURGE segment [{offset}, {offset + size}) exceeds the expected size {outputSize}.");
            }

            if (outputPos < offset)
            {
                outputPos = (int)offset; // zero-fill
            }

            if (streamEnd - inputPos < size)
            {
                throw new RvzFormatException(
                    $"PURGE stream is truncated inside a segment at byte {inputPos}.");
            }

            stream.Slice(inputPos, (int)size).CopyTo(output.AsSpan(outputPos));
            inputPos += (int)size;
            outputPos += (int)size;
        }

        return output;
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }
}
