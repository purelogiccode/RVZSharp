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
        if (listCount == 0)
        {
            return ([], 0);
        }

        var lists = new HashExceptionEntry[listCount][];
        var position = 0;

        for (var listIndex = 0; listIndex < listCount; listIndex++)
        {
            if (position + 2 > data.Length)
            {
                throw new RvzFormatException(
                    $"Truncated exception list {listIndex}: need 2 bytes for the count, "
                    + $"only {data.Length - position} available.");
            }

            var count = (ushort)((data[position] << 8) | data[position + 1]);
            position += 2;

            var listSize = checked(count * HashExceptionEntry.Size);
            if (alignTo4 && listIndex == listCount - 1)
            {
                listSize = (listSize + position + 3) & ~3;
                listSize -= position;
            }

            if (position + listSize > data.Length)
            {
                throw new RvzFormatException(
                    $"Truncated exception list {listIndex}: declares {count} exceptions "
                    + $"({listSize} bytes), only {data.Length - position} available.");
            }

            var entries = new HashExceptionEntry[count];
            for (var i = 0; i < count; i++)
            {
                entries[i] = HashExceptionEntry.Parse(data.Slice(position, HashExceptionEntry.Size));
                position += HashExceptionEntry.Size;
            }

            if (alignTo4 && listIndex == listCount - 1)
            {
                position = (position + 3) & ~3;
            }

            lists[listIndex] = entries;
        }

        return (lists, position);
    }
}
