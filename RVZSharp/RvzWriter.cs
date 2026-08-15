using System.Security.Cryptography;
using RVZSharp.Interfaces;
using RVZSharp.Chunks;
using RVZSharp.Compression;
using RVZSharp.Models;
using RVZSharp.Packing;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>Options for <see cref="RvzWriter.Write"/>.</summary>
public sealed record RvzWriteOptions
{
    public static readonly RvzWriteOptions Default = new();

    /// <summary>Compression method (Dolphin's default: Zstandard).</summary>
    public CompressionType Compression { get; init; } = CompressionType.Zstd;

    /// <summary>Compression level (1-9; Zstandard allows up to 22).</summary>
    public int CompressionLevel { get; init; } = 3;

    /// <summary>Chunk size: a power of two between 32 KiB and 2 MiB (Dolphin's default: 2 MiB).</summary>
    public int ChunkSize { get; init; } = (int)WiaDisc.GroupSize;

    /// <summary>Whether to apply the RVZ packing (junk detection) stage.</summary>
    public bool Packing { get; init; } = true;
}

/// <summary>
/// Encodes any decoded disc image (plain ISO or a legacy format via <see cref="IBlobReader"/>)
/// into the RVZ format, mirroring Dolphin's ConvertToWIAOrRVZ: Wii partition data is stored
/// decrypted with hash exceptions, raw data is stored as-is, and (optionally) PRNG junk is
/// packed with a recovered seed.
/// </summary>
public static class RvzWriter
{
    private const ulong SectorSize = WiaDisc.SectorSize;
    private const ulong GroupTotalSize = WiaDisc.GroupSize;
    private const ulong GroupDataSize = 0x1F0000;
    private const ulong DiscHeaderSize = WiaDisc.DiscHeaderSize; // 0x80

    private sealed class AreaEntry
    {
        public required ulong Offset { get; init; }
        public required ulong Size { get; init; }
        public bool IsPartition { get; init; }
        public required Partition Partition { get; init; }
        public int GroupIndex { get; set; }
        public ulong Groups { get; set; }
    }

    /// <summary>
    /// Decorates an <see cref="IBlobReader"/> with progress reporting and cancellation.
    /// All input reads in <see cref="Write"/> flow through <see cref="ReadAt"/>, so wrapping
    /// the input is enough to observe the whole conversion (the reported fraction is clamped
    /// to 1.0; header and table re-reads can push the byte count past the image size).
    /// </summary>
    private sealed class ProgressReader : IBlobReader
    {
        private readonly IBlobReader _inner;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _cancellationToken;
        private long _bytesServed;

        public ProgressReader(IBlobReader inner, IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            _inner = inner;
            _progress = progress;
            _cancellationToken = cancellationToken;
        }

        public BlobType Type => _inner.Type;
        public long Length => _inner.Length;
        public int BlockSize => _inner.BlockSize;

        public int ReadAt(long position, Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = _inner.ReadAt(position, buffer);
            if (read > 0)
            {
                _bytesServed += read;
                _progress?.Report(Math.Min(1.0, (double)_bytesServed / Length));
            }

            return read;
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// Writes <paramref name="input"/> as an RVZ file to <paramref name="output"/>.
    /// </summary>
    /// <param name="input">Any decoded disc image (plain ISO or a legacy container).</param>
    /// <param name="output">Destination stream (not disposed by this method).</param>
    /// <param name="options">Writer options; <see cref="RvzWriteOptions.Default"/> when null.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the input bytes processed.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between group reads.</param>
    /// <exception cref="ArgumentException">Invalid chunk size or input too small.</exception>
    /// <exception cref="RvzUnsupportedException">PURGE compression requested (WIA-only method).</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static void Write(IBlobReader input, Stream output, RvzWriteOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= RvzWriteOptions.Default;
        // Dolphin's rule (DiscUtils.cpp:210-236): at least 32 KiB; below 2 MiB the chunk
        // size must be a power of two, at 2 MiB either rule applies, and above 2 MiB it
        // must be a multiple of 2 MiB (e.g. 6 MiB is valid).
        var chunkSize = options.ChunkSize;
        if (chunkSize < 0x8000 ||
            (chunkSize < (int)WiaDisc.GroupSize && (chunkSize & (chunkSize - 1)) != 0) ||
            (chunkSize > (int)WiaDisc.GroupSize && chunkSize % (int)WiaDisc.GroupSize != 0))
        {
            throw new ArgumentException(
                "Chunk size must be at least 32 KiB: a power of two below 2 MiB, or a multiple of 2 MiB.",
                nameof(options));
        }

        if (options.Compression == CompressionType.Purge)
        {
            // PURGE is a WIA-only method; the RVZ format does not support it.
            throw new RvzUnsupportedException("PURGE compression is not supported for RVZ files.");
        }

        // Capture the container reference BEFORE the ProgressReader wrap below: the wrap
        // would hide the RvzReader type and lose the container-key preference for callers
        // that pass progress or a cancellation token.
        var containerRz = input as RvzReader;
        if (progress is not null || cancellationToken.CanBeCanceled)
        {
            input = new ProgressReader(input, progress, cancellationToken);
        }

        var (encoder, props) = CompressionEncoderFactory.Create(options.Compression, options.CompressionLevel);
        var isoSize = (ulong)input.Length;
        if (isoSize < DiscHeaderSize)
        {
            throw new ArgumentException(
                $"Input is too small to be a disc image ({isoSize} bytes).", nameof(input));
        }

        var discHeader = new byte[DiscHeaderSize];
        input.ReadAt(0, discHeader);

        var isWii = WiiVolume.IsWiiDisc(input) && WiiVolume.HasWiiHashes(input) &&
                    WiiVolume.HasWiiEncryption(input);

        // disc_type describes the volume, independent of how the data is encoded (Dolphin:
        // WIABlob.cpp:1989-1996): an unhashed/unencrypted Wii disc is still a Wii disc (2),
        // and unrecognized volumes get 0.
        var discType = WiiVolume.IsWiiDisc(input)
            ? (uint)DiscType.Wii
            : ReadBe32(discHeader, 0x18) == WiiVolume.GC_MAGIC ? (uint)DiscType.GameCube : (uint)DiscType.Unknown;

        // Build the data areas in disc order (Dolphin: SetUpDataEntriesForWriting).
        var areas = new List<AreaEntry>();
        if (isWii)
        {
            // The input container (RVZ/WIA) stores the authoritative partition keys in its own
            // partition table (wia_part_t.part_key). The ticket on the decoded disc may carry a
            // different key (e.g. re-signed tickets on some No-Intro dumps), so prefer the
            // container keys whenever the input is an RVZ/WIA file, and fall back to the disc
            // ticket key for plain ISO inputs (Dolphin: VolumeWii::GetPartitions ticket key).
            var containerKeys = new Dictionary<long, byte[]>();
            if (containerRz is not null)
            {
                // Register every non-empty segment start (segment 0, or segment 1 when
                // segment 0 is empty): the lookup below uses the partition's data start,
                // which is the first non-empty segment's start.
                foreach (var p in containerRz.Partitions)
                {
                    foreach (var segment in p.Data)
                    {
                        if (segment.NumSectors != 0)
                        {
                            containerKeys[(long)segment.FirstSector * WiaDisc.SectorSize] = p.Key;
                        }
                    }
                }
            }

            ulong lastRawOffset = 0;
            foreach (var partition in WiiVolume.GetPartitions(input))
            {
                // Prefer the container's partition-table key over the disc ticket key.
                var key = containerKeys.TryGetValue(
                    (long)(partition.Offset + partition.DataOffset), out var containerKey)
                    ? containerKey
                    : partition.Key;
                var effective = new Partition
                {
                    Offset = partition.Offset,
                    Type = partition.Type,
                    DataOffset = partition.DataOffset,
                    DataSize = partition.DataSize,
                    Key = key,
                };

                // Partitions overlapping the data already encoded (e.g. update partitions)
                // are skipped, exactly like Dolphin (WIABlob.cpp:967-971). Skipping never
                // advances lastRawOffset, so the skipped region stays covered as raw data.
                if (partition.Offset < lastRawOffset)
                {
                    continue;
                }

                var dataStart = partition.Offset + partition.DataOffset;
                // Clamp to the disc end like Dolphin (WIABlob.cpp:1006-1010); the clamped
                // tail below the sector boundary is left to the raw path.
                var dataEnd = Math.Min(dataStart + partition.DataSize, isoSize);
                var size = dataEnd - dataStart;

                // Genuinely unusable partitions are encoded as raw data (Dolphin's
                // behaviour): continue leaves lastRawOffset unchanged, so the region is
                // covered by a later raw entry. An odd size is NOT a skip reason — Dolphin
                // encodes the whole 0x8000-sector units and leaves the partial sector to
                // be covered as raw data.
                if (size < SectorSize || dataStart % SectorSize != 0)
                {
                    continue;
                }

                AddRawGap(areas, ref lastRawOffset, partition.Offset);
                AddRawGap(areas, ref lastRawOffset, dataStart);

                var fstOffset = WiiVolume.GetFstOffset(input, partition) ?? 0;
                var fstSize = WiiVolume.GetFstSize(input, partition) ?? 0;
                var fstEnd = partition.Offset + partition.DataOffset + fstOffset + fstSize;
                var splitPoint = Math.Min(dataStart + AlignUp(fstEnd - dataStart, GroupTotalSize), dataEnd);

                var size0 = AlignDown(splitPoint - dataStart, SectorSize);
                var size1 = AlignDown(dataEnd - splitPoint, SectorSize);
                if (size0 == 0 && size1 == 0)
                {
                    // Nothing to encode as partition data; the region stays raw.
                    continue;
                }

                if (size0 > 0)
                {
                    areas.Add(new AreaEntry { Offset = dataStart, Size = size0, IsPartition = true, Partition = effective });
                }

                if (size1 > 0)
                {
                    areas.Add(new AreaEntry { Offset = splitPoint, Size = size1, IsPartition = true, Partition = effective });
                }

                // The partition's unaligned tail (and any gap after segment 0) is covered
                // as raw data by the next gap: lastRawOffset is the rounded covered end,
                // not dataEnd (Dolphin derives last_partition_end_offset from the entry
                // sizes, WIABlob.cpp:1039-1042).
                lastRawOffset = Math.Max(lastRawOffset, splitPoint + size1);
            }

            AddRawArea(areas, lastRawOffset, isoSize - lastRawOffset);
        }
        else
        {
            // GameCube discs (and Wii discs without hashes/encryption): one raw area covering
            // everything after the disc header.
            areas.Add(new AreaEntry { Offset = DiscHeaderSize, Size = isoSize - DiscHeaderSize, Partition = default });
        }

        // Assign group indices in disc order (one group per chunk). Raw areas are read from
        // the sector-aligned offset, so their group count must cover the grown read size
        // (e.g. the first raw area starts 0x80 into the disc but is read from offset 0).
        var groupIndex = 0;
        foreach (var area in areas)
        {
            area.GroupIndex = groupIndex;
            var readSize = area.IsPartition
                ? area.Size
                : area.Size + (area.Offset - AlignDown(area.Offset, SectorSize));
            area.Groups = (readSize + (ulong)options.ChunkSize - 1) / (ulong)options.ChunkSize;
            groupIndex += (int)area.Groups;
        }

        // Process the areas in disc order: read, decrypt/transform, pack, compress.
        var groupData = new List<byte[]>();
        var groupEntries = new List<GroupEntry>();
        foreach (var area in areas)
        {
            if (area.IsPartition)
            {
                ProcessPartitionArea(input, area, options, encoder, groupData, groupEntries);
            }
            else
            {
                ProcessRawArea(input, area, options, encoder, groupData, groupEntries);
            }
        }

        // Tables. The group table is compressed like the group data, and its size depends on
        // the data offsets inside it; iterate until the layout is stable (same approach as the
        // RVZ test builder). The raw table is compressed once (its bytes are fixed).
        var partitionTable = BuildPartitionTable(areas);
        var rawTable = BuildRawTable(areas);
        var rawTableStored = CompressTable(encoder, rawTable);
        // The group table's stored size determines the group data offsets, which the table
        // itself encodes — a circular dependency (Dolphin avoids it by writing the tables
        // after the group data; here the tables come first, so we iterate). Each pass builds
        // the table from the previous stored size. A layout is valid as soon as the actual
        // stored size fits in the space its offsets assumed (stored <= layout size; the
        // difference becomes dead padding). Exact equality is not guaranteed — compressed
        // sizes are not monotone in the content and can cycle (bzip2) — so keep the tightest
        // valid layout seen and stop at the first perfect fit.
        var tableBase = (ulong)(WiaFileHead.Size + WiaDisc.Size + partitionTable.Length +
                                rawTableStored.Length);
        byte[] groupTableStored = [];
        var bestLayoutSize = 0;
        var bestPadding = long.MaxValue;
        var layoutSize = 0;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var running = AlignUp(tableBase + (ulong)layoutSize, 4);
            for (var i = 0; i < groupEntries.Count; i++)
            {
                var size = (ulong)groupEntries[i].StoredSize;
                groupEntries[i] = new GroupEntry(running, groupEntries[i].StoredSize,
                    groupEntries[i].UsesDiscCompression, groupEntries[i].RvzPackedSize);
                running += AlignUp(size, 4);
            }

            var stored = CompressTable(encoder, BuildGroupTable(groupEntries));
            if (stored.Length <= layoutSize)
            {
                var padding = layoutSize - stored.Length;
                if (padding < bestPadding)
                {
                    groupTableStored = stored;
                    bestLayoutSize = layoutSize;
                    bestPadding = padding;
                }

                if (padding == 0)
                {
                    break;
                }
            }

            layoutSize = stored.Length;
        }

        if (bestPadding == long.MaxValue)
        {
            throw new RvzFormatException("The group table layout did not converge.");
        }

        // Write the file: head + disc struct + tables + group data. The caller owns the
        // output stream, so it is not disposed here.
        var file = output;
        var partOffset = (ulong)(WiaFileHead.Size + WiaDisc.Size);
        var rawOffset = partOffset + (ulong)partitionTable.Length;
        var groupOffset = rawOffset + (ulong)rawTableStored.Length;
        var rawCount = (uint)areas.Count(a => !a.IsPartition);
        var partitionCount = (uint)areas.Where(a => a.IsPartition)
            .Select(a => a.Partition.Offset).Distinct().Count();

        var discStruct = BuildDiscStruct(discType, options, props, discHeader,
            partitionCount, rawCount, partOffset, rawOffset,
            rawTableStored.Length, groupOffset, groupTableStored.Length, groupEntries.Count);
        SHA1.HashData(partitionTable).CopyTo(discStruct, 0xA0);

        var head = BuildFileHead(isoSize, discStruct);
        file.Write(head);
        file.Write(discStruct);
        file.Write(partitionTable);
        file.Write(rawTableStored);
        file.Write(groupTableStored);

        // Pad to the aligned group-data start. The group offsets in the table were computed
        // from bestLayoutSize (which may exceed the stored table size when the table had to
        // fit with padding), so pad to that, not to groupTableStored.Length.
        var alignedStart = AlignUp((ulong)(WiaFileHead.Size + WiaDisc.Size + partitionTable.Length +
                                           rawTableStored.Length) + (ulong)bestLayoutSize, 4);
        var pad = (int)(alignedStart - (ulong)file.Position);
        if (pad > 0)
        {
            file.Write(new byte[pad]);
        }

        var written = (ulong)(WiaFileHead.Size + WiaDisc.Size + partitionTable.Length +
                              rawTableStored.Length + groupTableStored.Length + pad);
        for (var i = 0; i < groupData.Count; i++)
        {
            var data = groupData[i];
            file.Write(data);
            written += (ulong)data.Length;
            var padding = (4 - data.Length % 4) % 4;
            if (padding > 0)
            {
                file.Write(new byte[padding]);
                written += (uint)padding;
            }
        }

        // The file head's rvz_file_size is only known after writing everything.
        file.Position = 0;
        file.Write(BuildFileHead(isoSize, discStruct, file.Length));
    }

    private static void AddRawGap(List<AreaEntry> areas, ref ulong lastRawOffset, ulong offset)
    {
        if (offset > lastRawOffset)
        {
            AddRawArea(areas, lastRawOffset, offset - lastRawOffset);
        }

        lastRawOffset = Math.Max(lastRawOffset, offset);
    }

    private static void AddRawArea(List<AreaEntry> areas, ulong offset, ulong size)
    {
        // Dolphin's AddRawDataEntry skips the first 0x80 bytes of the disc: they live in the
        // disc struct's disc_header (WIABlob.cpp:902-906), and the reader serves them from
        // there. Only the first Wii raw gap (which starts at 0) is affected; the reader's
        // alignment growth (TableParser) maps the entry back to the same bytes.
        const ulong SkipSize = WiiVolume.DiscHeaderSize; // 0x80
        var skip = offset < SkipSize ? Math.Min(SkipSize - offset, size) : 0;
        offset += skip;
        size -= skip;

        if (size == 0)
        {
            return;
        }

        areas.Add(new AreaEntry { Offset = offset, Size = size, Partition = default });
    }

    private static void ProcessRawArea(IBlobReader input, AreaEntry area, RvzWriteOptions options,
        ICompressionEncoder encoder, List<byte[]> groupData, List<GroupEntry> groupEntries)
    {
        var chunkSize = (ulong)options.ChunkSize;
        var buffer = new byte[chunkSize];

        // Read from the sector-aligned offset with a grown size, matching the reader's
        // alignment fixup (Dolphin: data_offset = AlignDown(offset, 0x8000)).
        var dataOffset = AlignDown(area.Offset, SectorSize);
        var position = dataOffset;
        var remaining = area.Size + (area.Offset - dataOffset);
        while (remaining > 0)
        {
            var take = (int)Math.Min(chunkSize, remaining);
            if (input.ReadAt((long)position, buffer.AsSpan(0, take)) != take)
            {
                throw new RvzFormatException($"Read failed at 0x{position:X}.");
            }

            AddGroup(buffer.AsSpan(0, take), (long)dataOffset, options, encoder, [], groupData, groupEntries);
            position += (ulong)take;
            remaining -= (ulong)take;
            dataOffset += (ulong)take;
        }
    }

    private static void ProcessPartitionArea(IBlobReader input, AreaEntry area,
        RvzWriteOptions options, ICompressionEncoder encoder, List<byte[]> groupData,
        List<GroupEntry> groupEntries)
    {
        var blocksPerChunk = (int)(options.ChunkSize / 0x8000);
        var chunkPayload = blocksPerChunk * WiiHashCalculator.SectorDataSize;
        var extractor = new WiiPartitionExtractor(input, area.Partition.Key);
        var processedBlocks = 0UL;
        var blocksRemaining = area.Size / SectorSize;
        var dataOffsetInPartition = 0UL;

        while (blocksRemaining > 0)
        {
            // Read and decrypt one 2 MiB region (or the segment's remaining blocks).
            var regionBlocks = (int)Math.Min(64UL, blocksRemaining);
            var (data, exceptions) = extractor.ExtractRegion(
                (long)(area.Offset + processedBlocks * SectorSize), regionBlocks, blocksPerChunk);

            // Split the region into chunks (one group per chunk, like the reader expects).
            for (var chunk = 0; chunk * blocksPerChunk < regionBlocks; chunk++)
            {
                var chunkBlocks = Math.Min(blocksPerChunk, regionBlocks - chunk * blocksPerChunk);
                var chunkData = data.AsSpan(chunk * chunkPayload, chunkBlocks * WiiHashCalculator.SectorDataSize);

                // The region's exceptions whose block index falls into this chunk; convert
                // them to chunk-relative offsets (block_index_in_chunk × 0x400 + position).
                var chunkExceptions = exceptions
                    .Where(e => (e.Offset >> 10) >= chunk * blocksPerChunk &&
                                (e.Offset >> 10) < chunk * blocksPerChunk + chunkBlocks)
                    .Select(e => new HashExceptionEntry(
                        (ushort)(e.Offset - chunk * blocksPerChunk * 0x400), e.Hash))
                    .ToList();

                var lists = new List<byte>(2 + chunkExceptions.Count * 22);
                lists.Add((byte)(chunkExceptions.Count >> 8));
                lists.Add((byte)chunkExceptions.Count);
                foreach (var exception in chunkExceptions)
                {
                    lists.Add((byte)(exception.Offset >> 8));
                    lists.Add((byte)exception.Offset);
                    lists.AddRange(exception.Hash);
                }

                AddGroup(chunkData, (long)dataOffsetInPartition, options, encoder, lists,
                    groupData, groupEntries);
                dataOffsetInPartition += (ulong)chunkData.Length;
            }

            processedBlocks += (ulong)regionBlocks;
            blocksRemaining -= (ulong)regionBlocks;
        }
    }

    private static void AddGroup(ReadOnlySpan<byte> payload, long dataOffset, RvzWriteOptions options,
        ICompressionEncoder encoder, List<byte> exceptionLists, List<byte[]> groupData,
        List<GroupEntry> groupEntries)
    {
        // Pack the payload (junk detection) unless packing is disabled.
        var mainData = new List<byte>(payload.Length);
        uint packedSize = 0;
        if (options.Packing)
        {
            RvzPackingEncoder.Pack(payload, dataOffset, payload.Length, 1,
                allowJunkReuse: true, options.Compression != CompressionType.None, mainData,
                ref packedSize);
        }
        else
        {
            mainData.AddRange(payload);
        }

        // A chunk whose data is all zeroes (and has no exceptions) becomes a zero group:
        // nothing is stored (a group size of 0 means "all zeroes" in the format).
        if (exceptionLists.Count == 0 && IsAllZero(payload))
        {
            groupData.Add([]);
            groupEntries.Add(new GroupEntry(0, 0, false, 0));
            return;
        }

        var listBytes = exceptionLists.ToArray();
        var compressedExceptionLists = (int)options.Compression > (int)CompressionType.Purge;

        byte[] stored;
        bool compressed;
        if (options.Compression == CompressionType.None)
        {
            stored = Concat(Pad4(listBytes), mainData.ToArray());
            compressed = false;
        }
        else
        {
            var toCompress = compressedExceptionLists
                ? Concat(listBytes, mainData.ToArray())
                : mainData.ToArray();
            if (!compressedExceptionLists)
            {
                encoder.AddPrecedingData(listBytes);
            }

            var compressedBytes = encoder.Compress(toCompress);
            var uncompressedSize = (ulong)mainData.Count + (compressedExceptionLists
                ? AlignUp((ulong)listBytes.Length, 4)
                : 0);
            compressed = (ulong)compressedBytes.Length < uncompressedSize;
            stored = compressed
                ? compressedBytes
                : Concat(Pad4(listBytes), mainData.ToArray());
        }

        groupData.Add(stored);
        groupEntries.Add(new GroupEntry(0, (uint)stored.Length, compressed, packedSize));
    }

    private static byte[] BuildPartitionTable(List<AreaEntry> areas)
    {
        using var output = new MemoryStream();
        var partitions = areas.Where(a => a.IsPartition).ToList();
        for (var i = 0; i < partitions.Count;)
        {
            var partition = partitions[i].Partition;
            var area0 = partitions[i];
            var area1 = i + 1 < partitions.Count && partitions[i + 1].Partition.Offset == partition.Offset
                ? partitions[i + 1]
                : null;

            // wia_part_t: part_key[16], then two wia_part_data_t (first_sector, n_sectors,
            // group_index, n_groups) — segment 0 and segment 1.
            output.Write(partition.Key);

            WriteBe32(output, (uint)(area0.Offset / SectorSize));
            WriteBe32(output, (uint)(area0.Size / SectorSize));
            WriteBe32(output, (uint)area0.GroupIndex);
            WriteBe32(output, (uint)area0.Groups);

            if (area1 != null)
            {
                WriteBe32(output, (uint)(area1.Offset / SectorSize));
                WriteBe32(output, (uint)(area1.Size / SectorSize));
                WriteBe32(output, (uint)area1.GroupIndex);
                WriteBe32(output, (uint)area1.Groups);
            }
            else
            {
                WriteBe32(output, 0);
                WriteBe32(output, 0);
                WriteBe32(output, 0);
                WriteBe32(output, 0);
            }

            i += area1 != null ? 2 : 1;
        }

        return output.ToArray();
    }

    private static byte[] BuildRawTable(List<AreaEntry> areas)
    {
        using var output = new MemoryStream();
        foreach (var area in areas)
        {
            if (area.IsPartition)
            {
                continue;
            }

            WriteBe64(output, area.Offset);
            WriteBe64(output, area.Size);
            WriteBe32(output, (uint)area.GroupIndex);
            // number_of_groups: Dolphin's reader loops i < number_of_groups (WIABlob.cpp:508)
            // and grows the area by offset % 0x8000, so the count must cover the groups this
            // writer actually emitted for the area (area.Groups, the sector-aligned read size).
            WriteBe32(output, (uint)area.Groups);
        }

        return output.ToArray();
    }

    private static byte[] BuildGroupTable(List<GroupEntry> entries)
    {
        using var output = new MemoryStream();
        foreach (var entry in entries)
        {
            WriteBe32(output, (uint)(entry.FileOffset >> 2));
            WriteBe32(output, entry.StoredSize | (entry.UsesDiscCompression ? 0x80000000u : 0));
            WriteBe32(output, entry.RvzPackedSize);
        }

        return output.ToArray();
    }

    private static byte[] CompressTable(ICompressionEncoder encoder, byte[] table)
    {
        if (encoder is NoneEncoder)
        {
            return table;
        }

        return encoder.Compress(table);
    }

    private static byte[] BuildDiscStruct(uint discType, RvzWriteOptions options, byte[] props,
        byte[] discHeader, uint partitionCount, uint rawCount, ulong partOffset, ulong rawOffset,
        int rawTableSize, ulong groupOffset, int groupTableSize, int groupCount)
    {
        var disc = new byte[WiaDisc.Size];
        WriteBe32(disc, 0x00, discType);
        WriteBe32(disc, 0x04, (uint)options.Compression);
        // The level is an s32 in the format (informative only); write the full value so
        // negative Zstd fast levels survive (Dolphin: swap32(compression_level)).
        WriteBe32(disc, 0x08, unchecked((uint)options.CompressionLevel));
        WriteBe32(disc, 0x0C, (uint)options.ChunkSize);
        discHeader.CopyTo(disc, 0x10);

        WriteBe32(disc, 0x90, partitionCount);
        WriteBe32(disc, 0x94, 0x30);
        WriteBe64(disc, 0x98, partOffset);
        // 0xA0: partition table hash (patched by the caller).
        WriteBe32(disc, 0xB4, rawCount);
        WriteBe64(disc, 0xB8, rawOffset);
        WriteBe32(disc, 0xC0, (uint)rawTableSize);
        WriteBe32(disc, 0xC4, (uint)groupCount);
        WriteBe64(disc, 0xC8, groupOffset);
        WriteBe32(disc, 0xD0, (uint)groupTableSize);
        disc[0xD4] = (byte)props.Length;
        props.CopyTo(disc, 0xD5);
        return disc;
    }

    private static byte[] BuildFileHead(ulong isoSize, byte[] discStruct, long? fileSize = null)
    {
        var head = new byte[WiaFileHead.Size];
        "RVZ\x01"u8.CopyTo(head);
        WriteBe32(head, 0x04, 0x01000000);
        WriteBe32(head, 0x08, 0x00030000);
        WriteBe32(head, 0x0C, WiaDisc.Size);
        SHA1.HashData(discStruct).CopyTo(head, 0x10);
        WriteBe64(head, 0x24, isoSize);
        WriteBe64(head, 0x2C, (ulong)(fileSize ?? 0));
        SHA1.HashData(head.AsSpan(0, 0x34)).CopyTo(head, 0x34);
        return head;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static byte[] Pad4(byte[] data)
    {
        var padding = (4 - data.Length % 4) % 4;
        return padding == 0 ? data : Concat(data, new byte[padding]);
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private static ulong AlignDown(ulong value, ulong alignment) => value / alignment * alignment;

    private static void WriteBe32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBe64(Stream stream, ulong value)
    {
        WriteBe32(stream, (uint)(value >> 32));
        WriteBe32(stream, (uint)value);
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteBe64(byte[] data, int offset, ulong value)
    {
        WriteBe32(data, offset, (uint)(value >> 32));
        WriteBe32(data, offset + 4, (uint)value);
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
