namespace RVZSharp.Packing;

/// <summary>
/// The RVZ packing encoder (Dolphin: RVZPack). Scans the chunk's data for regions that look
/// like Lagged-Fibonacci PRNG junk (recovering a seed with
/// <see cref="LaggedFibonacciGenerator.GetSeed"/>), and emits a segment stream: literal
/// segments (u32 BE size, no MSB) and junk segments (u32 BE size | 0x80000000 followed by the
/// 68-byte seed). A chunk with no junk at all is stored without any size headers (the packed
/// size stays 0, which tells the reader the data is unpacked).
/// </summary>
public static class RvzPackingEncoder
{
    public const int SeedSize = LaggedFibonacciGenerator.SeedSize;
    public const int BlockSize = 0x8000;
    private const int JunkReuseThreshold = SeedSize;

    /// <summary>
    /// Appends the packed segments for one 2 MiB group (or raw chunk) to
    /// <paramref name="mainData"/>.
    /// </summary>
    /// <param name="groupData">The chunk/group payload.</param>
    /// <param name="dataOffset">Offset of this data within its area (disc-relative for raw
    /// data, partition-data-relative for partitions), used for the PRNG skip.</param>
    /// <param name="bytesPerChunk">Chunk payload size; for partitions this is the payload per
    /// chunk (chunk_size × 0x7C00 / 0x8000).</param>
    /// <param name="chunks">Number of chunks in the enclosing data entry.</param>
    /// <param name="allowJunkReuse">True when chunks cannot be re-paired (2 MiB chunks).</param>
    /// <param name="compression">Whether the disc uses a compression method (zero runs are only
    /// packed as zero-junk when this is false).</param>
    /// <param name="mainData">The segment stream is appended here.</param>
    /// <param name="packedSize">Accumulated packed size (number of bytes of mainData that are
    /// covered by the segment stream).</param>
    public static void Pack(ReadOnlySpan<byte> groupData, long dataOffset, int bytesPerChunk,
        long chunks, bool allowJunkReuse, bool compression, List<byte> mainData, ref uint packedSize)
    {
        // Scan phase: find junk regions (Dolphin: the junk_info map, keyed by end offset).
        var junkInfo = new SortedDictionary<long, (long Start, byte[] Seed)>();
        long position = 0;
        var runningOffset = dataOffset;
        var totalSize = groupData.Length;
        while (position < totalSize)
        {
            // Count leading zeroes (only packable as zero-junk when uncompressed).
            var zeroes = 0L;
            while (position + zeroes < totalSize && groupData[(int)(position + zeroes)] == 0)
            {
                zeroes++;
            }

            if (!compression && zeroes > JunkReuseThreshold)
            {
                junkInfo[position + zeroes] = (position, new byte[SeedSize]);
            }

            position += zeroes;
            runningOffset += zeroes;
            if (position == totalSize)
            {
                break;
            }

            var aligned = ((runningOffset + 1 + BlockSize - 1) / BlockSize) * BlockSize;
            var bytesToRead = (int)Math.Min(aligned - runningOffset, totalSize - position);

            var (seed, bytesReconstructed) = LaggedFibonacciGenerator.GetSeed(
                groupData.Slice((int)position, bytesToRead), bytesToRead, runningOffset % BlockSize);
            if (bytesReconstructed > 0)
            {
                junkInfo[position + bytesReconstructed] = (position, seed);
            }

            position += bytesToRead;
            runningOffset += bytesToRead;
        }

        // Emission phase: one chunk per iteration.
        for (var chunk = 0L; chunk < chunks; chunk++)
        {
            var currentOffset = chunk * bytesPerChunk;
            var endOffset = Math.Min(currentOffset + bytesPerChunk, totalSize);
            // Dolphin disables the "no junk → store without size headers" shortcut for
            // multipart data entries (first_loop_iteration = !multipart, WIABlob.cpp:1237):
            // every chunk of a multi-chunk entry gets a proper segment stream, so the
            // reader can always tell where one chunk's data ends.
            var firstLoopIteration = chunks <= 1;

            while (currentOffset < endOffset)
            {
                var nextJunkStart = endOffset;
                var nextJunkEnd = endOffset;
                byte[]? seed = null;
                if (endOffset - currentOffset > JunkReuseThreshold)
                {
                    foreach (var (junkEnd, junk) in junkInfo)
                    {
                        if (junkEnd <= currentOffset + JunkReuseThreshold)
                        {
                            continue;
                        }

                        if (junk.Start + JunkReuseThreshold < endOffset)
                        {
                            nextJunkStart = Math.Max(currentOffset, junk.Start);
                            nextJunkEnd = Math.Min(endOffset, junkEnd);
                            seed = junk.Seed;
                        }

                        break;
                    }
                }

                if (firstLoopIteration)
                {
                    if (nextJunkStart == endOffset)
                    {
                        // Storing this chunk with RVZ packing would be inefficient, so store
                        // it without any size headers (rvz_packed_size stays 0).
                        mainData.AddRange(groupData.Slice((int)currentOffset, (int)(endOffset - currentOffset)));
                        break;
                    }

                    firstLoopIteration = false;
                }

                var nonJunkBytes = nextJunkStart - currentOffset;
                if (nonJunkBytes > 0)
                {
                    mainData.Add((byte)(nonJunkBytes >> 24));
                    mainData.Add((byte)(nonJunkBytes >> 16));
                    mainData.Add((byte)(nonJunkBytes >> 8));
                    mainData.Add((byte)nonJunkBytes);
                    mainData.AddRange(groupData.Slice((int)currentOffset, (int)nonJunkBytes));
                    currentOffset += nonJunkBytes;
                    packedSize += 4 + (uint)nonJunkBytes;
                }

                var junkBytes = nextJunkEnd - currentOffset;
                if (junkBytes > 0)
                {
                    mainData.Add((byte)(junkBytes >> 24 | 0x80));
                    mainData.Add((byte)(junkBytes >> 16));
                    mainData.Add((byte)(junkBytes >> 8));
                    mainData.Add((byte)junkBytes);
                    mainData.AddRange(seed!);
                    currentOffset += junkBytes;
                    packedSize += 4 + SeedSize;
                }
            }
        }
    }
}
