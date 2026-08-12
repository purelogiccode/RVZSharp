using RVZSharp.Compression;
using RVZSharp.Format;
using RVZSharp.IO;
using RVZSharp.Packing;

namespace RVZSharp.Chunks;

/// <summary>What is needed to decode one group chunk.</summary>
public readonly struct ChunkDecodeRequest
{
    public required RvzGroupEntry Group { get; init; }

    /// <summary>True for Wii partition chunks (they start with hash exception lists).</summary>
    public required bool IsPartition { get; init; }

    /// <summary>Expected payload size in bytes (chunk size, or less for the last chunk).</summary>
    public required int ExpectedSize { get; init; }

    /// <summary>
    /// Offset of this chunk's data: disc-relative for raw data, partition-data-relative for
    /// partitions. Used for the PRNG skip in RVZ packing.
    /// </summary>
    public required long DataOffset { get; init; }
}

/// <summary>The decoded content of one group chunk.</summary>
public readonly struct ChunkDecodeResult
{
    /// <summary>The chunk payload (exception lists and packing removed).</summary>
    public required byte[] Payload { get; init; }

    /// <summary>
    /// The parsed hash exception lists (partition chunks only; empty for raw data chunks).
    /// The lists themselves are not part of <see cref="Payload"/>.
    /// </summary>
    public HashExceptionEntry[][] ExceptionLists { get; init; } = [];

    public ChunkDecodeResult() { }
}

/// <summary>
/// Decodes one group chunk: reads the stored bytes, decompresses with the disc codec (or NONE),
/// strips the hash exception lists (partition chunks), decodes the RVZ packing, and returns the
/// payload. Follows Dolphin's WIARVZFileReader chunk logic. Reads are always bounded to the
/// exact number of bytes needed, which keeps the streaming codecs (notably LZMA) from having
/// to consume end-of-stream markers mid-read.
/// </summary>
public static class ChunkDecoder
{
    /// <summary>Number of exception lists per partition chunk (Dolphin: max(1, chunk/2 MiB)).</summary>
    public static int ExceptionListCount(int partitionChunkSize) =>
        Math.Max(1, partitionChunkSize / PartitionGroupDataSize);

    private const int PartitionGroupDataSize = 0x1F0000; // 0x7C00 * 64 (2 MiB minus hashes)

    public static ChunkDecodeResult DecodeChunk(Stream file, WiaDisc disc, ICompressionDecoder codec,
        ChunkDecodeRequest request)
    {
        var group = request.Group;
        var expectedSize = request.ExpectedSize;

        if (group.StoredSize == 0)
        {
            // Special case: all zeroes, empty exception lists.
            return new ChunkDecodeResult { Payload = new byte[expectedSize] };
        }

        try
        {
            return DecodeChunkCore(file, disc, codec, request, group, expectedSize);
        }
        catch (RvzException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ZstdSharp.ZstdException)
        {
            throw new RvzFormatException($"Failed to decode a group chunk: {e.Message}", e);
        }
    }

    private static ChunkDecodeResult DecodeChunkCore(Stream file, WiaDisc disc,
        ICompressionDecoder codec, ChunkDecodeRequest request, RvzGroupEntry group, int expectedSize)
    {
        var exceptionListCount = request.IsPartition ? ExceptionListCount(expectedSize) : 0;

        using Stream input = group.UsesDiscCompression
            ? OpenDecompressor(file, disc, codec, group)
            : new SectionStream(file, (long)group.FileOffset, group.StoredSize);

        // Exception lists are padded to 4 bytes only when the effective compression method
        // (the flag clears to NONE) is NONE — regardless of the flag bit itself.
        var effectiveCompression = group.UsesDiscCompression
            ? disc.Compression
            : CompressionType.None;
        var exceptionLists = ParseExceptionLists(input, exceptionListCount,
            alignTo4: effectiveCompression == CompressionType.None);

        byte[] payload;
        if (group.RvzPackedSize != 0)
        {
            var packed = ReadExactly(input, (int)group.RvzPackedSize, "RVZ packed data");
            payload = DecodePacking(packed, expectedSize, request.DataOffset);
        }
        else
        {
            payload = ReadExactly(input, expectedSize, "group payload");
        }

        return new ChunkDecodeResult { Payload = payload, ExceptionLists = exceptionLists };
    }

    private static Stream OpenDecompressor(Stream file, WiaDisc disc, ICompressionDecoder codec,
        RvzGroupEntry group)
    {
        var section = new SectionStream(file, (long)group.FileOffset, group.StoredSize);
        try
        {
            return codec.CreateDecompressor(
                section, disc.ComprData.AsSpan(0, disc.ComprDataLen), group.StoredSize, -1);
        }
        catch
        {
            section.Dispose();
            throw;
        }
    }

    private static HashExceptionEntry[][] ParseExceptionLists(Stream input, int listCount, bool alignTo4)
    {
        if (listCount == 0)
        {
            return [];
        }

        var lists = new HashExceptionEntry[listCount][];
        var totalBytes = 0;
        for (var listIndex = 0; listIndex < listCount; listIndex++)
        {
            var countBytes = ReadExactly(input, 2, "exception list count");
            var count = (ushort)((countBytes[0] << 8) | countBytes[1]);

            var entries = new HashExceptionEntry[count];
            for (var i = 0; i < count; i++)
            {
                var entryBytes = ReadExactly(input, HashExceptionEntry.Size, "hash exception");
                entries[i] = HashExceptionEntry.Parse(entryBytes);
            }

            lists[listIndex] = entries;
            totalBytes += 2 + count * HashExceptionEntry.Size;

            if (alignTo4 && listIndex == listCount - 1)
            {
                var padding = (4 - totalBytes % 4) % 4;
                if (padding > 0)
                {
                    ReadExactly(input, padding, "exception list padding");
                    totalBytes += padding;
                }
            }

            if (totalBytes > listCount * ExceptionListParser.MaxBytesPerList)
            {
                throw new RvzFormatException("More hash exceptions than expected.");
            }
        }

        return lists;
    }

    private static byte[] DecodePacking(byte[] packed, int expectedSize, long dataOffset)
    {
        using var input = new MemoryStream(packed, writable: false);
        using var decoder = new RvzPackingDecoder(input, dataOffset);
        return ReadExactly(decoder, expectedSize, "RVZ packing output");
    }

    private static byte[] ReadExactly(Stream stream, int count, string what)
    {
        var output = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(output, total, count - total);
            if (read <= 0)
            {
                throw new RvzFormatException(
                    $"Truncated {what}: got {total} of {count} bytes.");
            }

            total += read;
        }

        return output;
    }
}
