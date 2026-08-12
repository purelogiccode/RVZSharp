using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Chunks;
using RVZSharp.Compression;
using RVZSharp.Format;
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

    /// <summary>Writes <paramref name="input"/> as an RVZ file to <paramref name="output"/>.</summary>
    public static void Write(IBlobReader input, Stream output, RvzWriteOptions? options = null)
    {
        options ??= RvzWriteOptions.Default;
        if (options.ChunkSize < 0x8000 || options.ChunkSize > (int)WiaDisc.GroupSize ||
            (options.ChunkSize & (options.ChunkSize - 1)) != 0)
        {
            throw new ArgumentException(
                "Chunk size must be a power of two between 32 KiB and 2 MiB.", nameof(options));
        }

        if (options.Compression == CompressionType.Purge)
        {
            // PURGE is a WIA-only method; the RVZ format does not support it.
            throw new RvzUnsupportedException("PURGE compression is not supported for RVZ files.");
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

        // Build the data areas in disc order (Dolphin: SetUpDataEntriesForWriting).
        var areas = new List<AreaEntry>();
        if (isWii)
        {
            ulong lastRawOffset = 0;
            foreach (var partition in WiiVolume.GetPartitions(input))
            {
                var dataStart = partition.Offset + partition.DataOffset;
                var dataEnd = Math.Min(dataStart + partition.DataSize, isoSize);
                var size = dataEnd - dataStart;

                // Invalid partitions are encoded as raw data (Dolphin's behaviour).
                if (size < SectorSize || size % SectorSize != 0 || dataStart % SectorSize != 0)
                {
                    lastRawOffset = Math.Max(lastRawOffset, partition.Offset + SectorSize);
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
                    lastRawOffset = Math.Max(lastRawOffset, dataEnd);
                    continue;
                }

                if (size0 > 0)
                {
                    areas.Add(new AreaEntry { Offset = dataStart, Size = size0, IsPartition = true, Partition = partition });
                }

                if (size1 > 0)
                {
                    areas.Add(new AreaEntry { Offset = splitPoint, Size = size1, IsPartition = true, Partition = partition });
                }

                lastRawOffset = Math.Max(lastRawOffset, dataEnd);
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
        byte[] groupTableStored = [];
        var converged = false;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var groupDataStart = (ulong)(WiaFileHead.Size + WiaDisc.Size + partitionTable.Length +
                                         rawTableStored.Length + groupTableStored.Length);
            var running = AlignUp(groupDataStart, 4);
            for (var i = 0; i < groupEntries.Count; i++)
            {
                var size = (ulong)groupEntries[i].StoredSize;
                groupEntries[i] = new GroupEntry(running, groupEntries[i].StoredSize,
                    groupEntries[i].UsesDiscCompression, groupEntries[i].RvzPackedSize);
                running += AlignUp(size, 4);
            }

            var nextStored = CompressTable(encoder, BuildGroupTable(groupEntries));
            if (nextStored.Length == groupTableStored.Length)
            {
                groupTableStored = nextStored; // converged: keep the table for these offsets
                converged = true;
                break;
            }

            groupTableStored = nextStored;
        }

        if (!converged)
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

        var discStruct = BuildDiscStruct(isWii, options, props, discHeader,
            partitionCount, rawCount, partOffset, rawOffset,
            rawTableStored.Length, groupOffset, groupTableStored.Length, groupEntries.Count);
        SHA1.HashData(partitionTable).CopyTo(discStruct, 0xA0);

        var head = BuildFileHead(isoSize, discStruct);
        file.Write(head);
        file.Write(discStruct);
        file.Write(partitionTable);
        file.Write(rawTableStored);
        file.Write(groupTableStored);

        // Pad to the aligned group-data start (the layout assumes 4-byte alignment).
        var alignedStart = AlignUp((ulong)(WiaFileHead.Size + WiaDisc.Size + partitionTable.Length +
                                           rawTableStored.Length + groupTableStored.Length), 4);
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
            WriteBe32(output, 0); // padding to a 24-byte entry
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

    private static byte[] BuildDiscStruct(bool isWii, RvzWriteOptions options, byte[] props,
        byte[] discHeader, uint partitionCount, uint rawCount, ulong partOffset, ulong rawOffset,
        int rawTableSize, ulong groupOffset, int groupTableSize, int groupCount)
    {
        var disc = new byte[WiaDisc.Size];
        WriteBe32(disc, 0x00, isWii ? (uint)DiscType.Wii : (uint)DiscType.GameCube);
        WriteBe32(disc, 0x04, (uint)options.Compression);
        WriteBe32(disc, 0x08, unchecked((uint)(sbyte)options.CompressionLevel));
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
}
