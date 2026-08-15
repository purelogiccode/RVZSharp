using RVZSharp.Compression;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.IO;
using RVZSharp.Packing;

namespace RVZSharp.Chunks;


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
    private const int GroupTotalSize = 0x200000; // one 2 MiB Wii group, incl. hashes

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
        catch (Exception e) when (e is IOException or InvalidDataException or ZstdSharp.ZstdException
                                 or ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            throw new RvzFormatException($"Failed to decode a group chunk: {e.Message}", e);
        }
    }

    private static ChunkDecodeResult DecodeChunkCore(Stream file, WiaDisc disc,
        ICompressionDecoder codec, ChunkDecodeRequest request, GroupEntry group, int expectedSize)
    {
        // The exception-list count is fixed by the FULL chunk size: Dolphin's writer stores
        // exception_lists_per_chunk = max(1, chunk_size / 2 MiB) lists for every partition
        // chunk, including the final partial one (WIABlob.cpp:1386-1387). Deriving it from the
        // truncated expectedSize of a partial chunk would parse too few lists and misread the
        // trailing lists as payload (relevant for chunk sizes > 2 MiB).
        var exceptionListCount = request.IsPartition
            ? ExceptionListCount((int)((long)disc.ChunkSize * PartitionGroupDataSize / GroupTotalSize))
            : 0;

        // PURGE is not a streaming codec: its data sits right after the exception lists in the
        // raw group bytes, so the whole group is read as one section and decoded in one go.
        var isPurge = group.UsesDiscCompression && disc.Compression == CompressionType.Purge;
        var usesDecompressor = group.UsesDiscCompression && !isPurge;

        using Stream input = usesDecompressor
            ? OpenDecompressor(file, disc, codec, group)
            : new SectionStream(file, (long)group.FileOffset, group.StoredSize);

        // Exception lists are padded to 4 bytes only when the effective compression method
        // (the flag clears to NONE for RVZ) is NONE or PURGE — the methods that store the
        // lists uncompressed (Dolphin: compressed_exception_lists = method > Purge).
        var effectiveCompression = group.UsesDiscCompression
            ? disc.Compression
            : CompressionType.None;
        var exceptionLists = ParseExceptionLists(input, exceptionListCount,
            alignTo4: effectiveCompression is CompressionType.None or CompressionType.Purge,
            out var listBytes);

        byte[] payload;
        if (isPurge)
        {
            payload = PurgeDecoder.Decode(ReadAll(input), listBytes, expectedSize);
        }
        else if (group.RvzPackedSize != 0)
        {
            var packed = ReadExactly(input, (int)group.RvzPackedSize, "RVZ packed data");
            payload = DecodePacking(packed, expectedSize, request.DataOffset);
        }
        else
        {
            payload = ReadExactly(input, expectedSize, "group payload");
        }

        // Dolphin rejects chunks whose decompressed output exceeds the expected size
        // (WIABlob.cpp:741-754): the (decompressed) stream must be exhausted right after
        // the payload. A stream that produced extra output fails this probe read.
        if (!isPurge && input.ReadByte() != -1)
        {
            throw new RvzFormatException(
                $"Group chunk decompressed to more than {expectedSize} bytes.");
        }

        return new ChunkDecodeResult { Payload = payload, ExceptionLists = exceptionLists };
    }

    private static Stream OpenDecompressor(Stream file, WiaDisc disc, ICompressionDecoder codec,
        GroupEntry group)
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

    private static HashExceptionEntry[][] ParseExceptionLists(Stream input, int listCount, bool alignTo4,
        out byte[] consumedBytes)
    {
        if (listCount == 0)
        {
            consumedBytes = [];
            return [];
        }

        using var consumed = new MemoryStream();
        var lists = new HashExceptionEntry[listCount][];
        var totalBytes = 0;
        for (var listIndex = 0; listIndex < listCount; listIndex++)
        {
            var countBytes = ReadExactly(input, 2, "exception list count");
            consumed.Write(countBytes);
            var count = (ushort)((countBytes[0] << 8) | countBytes[1]);

            var entries = new HashExceptionEntry[count];
            for (var i = 0; i < count; i++)
            {
                var entryBytes = ReadExactly(input, HashExceptionEntry.Size, "hash exception");
                consumed.Write(entryBytes);
                entries[i] = HashExceptionEntry.Parse(entryBytes);
            }

            lists[listIndex] = entries;
            totalBytes += 2 + count * HashExceptionEntry.Size;

            if (alignTo4 && listIndex == listCount - 1)
            {
                var padding = (4 - totalBytes % 4) % 4;
                if (padding > 0)
                {
                    var pad = ReadExactly(input, padding, "exception list padding");
                    consumed.Write(pad);
                    totalBytes += padding;
                }
            }

            if (totalBytes > listCount * ExceptionListParser.MaxBytesPerList)
            {
                throw new RvzFormatException("More hash exceptions than expected.");
            }
        }

        consumedBytes = consumed.ToArray();
        return lists;
    }

    private static byte[] DecodePacking(byte[] packed, int expectedSize, long dataOffset)
    {
        using var input = new MemoryStream(packed, writable: false);
        using var decoder = new RvzPackingDecoder(input, dataOffset);
        return ReadExactly(decoder, expectedSize, "RVZ packing output");
    }

    private static byte[] ReadAll(Stream stream)
    {
        var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
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
