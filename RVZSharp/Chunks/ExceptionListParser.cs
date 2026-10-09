using RVZSharp.Models;

namespace RVZSharp.Chunks;

/// <summary>
/// Parses the <c>wia_except_list_t</c> structs stored at the start of Wii partition chunks.
/// For the NONE compression method the lists are stored uncompressed before the data and the
/// end of the last list is padded to a 4-byte boundary; for compressed methods they are at the
/// start of the decompressed data with no padding.
/// </summary>
public static class ExceptionListParser
{
    /// <summary>Dolphin's reading limit: 52×64 exceptions per list (covers all hashes + padding).</summary>
    public const int MaxExceptionsPerList = 52 * 64; // 3328

    /// <summary>Maximum bytes one exception list can occupy (2 + 3328 × 22).</summary>
    public const int MaxBytesPerList = 2 + MaxExceptionsPerList * HashExceptionEntry.Size;

    /// <summary>
    /// Parses <paramref name="listCount"/> exception lists from the start of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Chunk data (decompressed for compressed methods, raw for NONE).</param>
    /// <param name="listCount">Number of lists expected (partition: max(1, chunkSize / 2 MiB)).</param>
    /// <param name="alignTo4">
    /// True for the NONE method: pad the end of the last list to a 4-byte boundary.
    /// </param>
    /// <returns>The parsed lists and the byte offset where the actual data starts.</returns>
    public static (HashExceptionEntry[][] Lists, int BytesUsed) Parse(
        ReadOnlySpan<byte> data, int listCount, bool alignTo4)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        var lists = Parse(stream, listCount, alignTo4, out var consumedBytes);
        return (lists, consumedBytes.Length);
    }

    /// <summary>
    /// Parses <paramref name="listCount"/> exception lists from a stream (the production path
    /// used by <see cref="ChunkDecoder"/>). Enforces Dolphin's per-list size budget before
    /// allocating or reading entries.
    /// </summary>
    /// <param name="input">The chunk stream, positioned at the first list.</param>
    /// <param name="listCount">Number of lists expected.</param>
    /// <param name="alignTo4">True for the NONE method: pad the last list to 4 bytes.</param>
    /// <param name="consumedBytes">The raw bytes the lists (including padding) occupied.</param>
    /// <returns>The parsed lists.</returns>
    internal static HashExceptionEntry[][] Parse(Stream input, int listCount, bool alignTo4,
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

            // Enforce the size budget before allocating/reading the entries so a hostile
            // count cannot request a large array (Dolphin: MAX_SIZE_PER_EXCEPTION_LIST).
            if (totalBytes + 2L + (long)count * HashExceptionEntry.Size >
                (long)listCount * MaxBytesPerList)
            {
                throw new RvzFormatException("More hash exceptions than expected.");
            }

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

            if (totalBytes > listCount * MaxBytesPerList)
            {
                throw new RvzFormatException("More hash exceptions than expected.");
            }
        }

        consumedBytes = consumed.ToArray();
        return lists;
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
                throw new RvzFormatException($"Truncated {what}.");
            }

            total += read;
        }

        return output;
    }
}
