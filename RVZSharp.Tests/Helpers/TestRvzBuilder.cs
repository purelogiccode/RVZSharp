using System.Security.Cryptography;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp.Tests.Helpers;

/// <summary>Specification for a synthetic disc image encoded by <see cref="TestRvzBuilder"/>.</summary>
public sealed class RvzSpec
{
    public CompressionType Compression { get; set; } = CompressionType.None;
    public uint ChunkSize { get; set; } = WiaDisc.GroupSize; // 2 MiB
    public DiscType DiscType { get; set; } = DiscType.GameCube;

    /// <summary>Bytes of raw data after the first 0x80 bytes and before any partition.</summary>
    public int RawSize { get; set; } = 3 * 0x8000;

    /// <summary>Bytes of raw data after the partition.</summary>
    public int RawTailSize { get; set; }

    /// <summary>Optional Wii partition.</summary>
    public PartitionSpec? Partition { get; set; }

    /// <summary>Global chunk indices that use RVZ packing (empty = no packing; ignored for WIA).</summary>
    public HashSet<int> PackedChunks { get; set; } = [];

    /// <summary>When true, builds a WIA file instead of an RVZ file (8-byte groups, no packing).</summary>
    public bool IsWia { get; set; }

    public int Seed { get; set; } = 1;
}

/// <summary>A Wii partition to encode.</summary>
public sealed class PartitionSpec
{
    public int SectorCount { get; set; } = 70;

    public byte[] Key { get; set; } = [.. Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i))];

    /// <summary>Hash exceptions per 2 MiB region (region-relative offsets).</summary>
    public HashExceptionEntry[][] Exceptions { get; set; } = [];
}

/// <summary>
/// A minimal RVZ writer for tests: builds a structurally valid RVZ file from an
/// <see cref="RvzSpec"/> following Dolphin's converter rules (aligned raw entries,
/// per-2 MiB partition regions, exception lists, optional RVZ packing).
/// </summary>
public static class TestRvzBuilder
{
    public static byte[] Build(RvzSpec spec)
    {
        return BuildWithIso(spec).Rvz;
    }

    public static (byte[] Rvz, byte[] Iso) BuildWithIso(RvzSpec spec)
    {
        var rng = new Random(spec.Seed);
        var discHeader = new byte[0x80];
        rng.NextBytes(discHeader);

        var chunkSize = (long)spec.ChunkSize;
        var sectorsPerChunk = chunkSize / 0x8000;
        var partitionPayloadPerChunk = sectorsPerChunk * 0x7C00;

        // --- Compute the disc layout ------------------------------------------------
        var partition = spec.Partition;
        var partitionOffset = 0L;
        var partitionEncrypted = Array.Empty<byte>();
        var partitionPayloads = new List<byte[]>();
        if (partition != null)
        {
            partitionOffset = AlignUp(0x80L + spec.RawSize, 0x8000);
            var totalSectors = (long)partition.SectorCount * 0x7C00;
            for (var c = 0; c * partitionPayloadPerChunk < totalSectors; c++)
            {
                var size = (int)Math.Min(partitionPayloadPerChunk, totalSectors - c * partitionPayloadPerChunk);
                partitionPayloads.Add(new byte[size]);
            }
            // (the actual data is filled below, chunk by chunk, so packed chunks get junk)
        }

        // The first raw entry covers everything up to the partition (no gaps on a real disc).
        var rawEntry1Size = partition == null
            ? spec.RawSize
            : (int)(partitionOffset - 0x80);
        var isoSize = partition == null
            ? 0x80L + spec.RawSize
            : partitionOffset + partition.SectorCount * 0x8000L + spec.RawTailSize;

        // --- Generate the chunk payloads (raw chunks include the disc header bytes) --
        // Packed chunks consist of a literal prefix followed by PRNG junk (that is what the
        // RVZ packing encodes), so the junk is generated up front.
        var payloads = new List<byte[]>();
        var rawEntryChunkCounts = new List<(int EntryIndex, int ChunkCount)>();
        var globalChunk = 0;

        AddRawEntry(0x80, rawEntry1Size, 0);
        if (partition != null)
        {
            // Partition payloads: packed chunks get literal + junk too.
            var packedPartition = spec.PackedChunks.Where(i => i >= globalChunk)
                .Select(i => i - globalChunk).ToHashSet();
            for (var c = 0; c < partitionPayloads.Count; c++)
            {
                if (packedPartition.Contains(c))
                {
                    var size = partitionPayloads[c].Length;
                    var split = size / 2;
                    var seedBytes = MakeSeed(spec.Seed ^ (int)(c * partitionPayloadPerChunk));
                    var junk = ReferencePrng.Generate(seedBytes, c * partitionPayloadPerChunk + split, size - split);
                    rng.NextBytes(partitionPayloads[c].AsSpan(0, split));
                    junk.CopyTo(partitionPayloads[c], split);
                }
                else
                {
                    rng.NextBytes(partitionPayloads[c]);
                }
            }

            payloads.AddRange(partitionPayloads);
            globalChunk += partitionPayloads.Count;
            AddRawEntry(partitionOffset + partition.SectorCount * 0x8000L, spec.RawTailSize, 1);
        }

        // --- Encrypt the partition (same exceptions as the chunk lists) ---------------
        if (partition != null)
        {
            var decrypted = new byte[partitionPayloads.Sum(p => p.Length)];
            var pos = 0;
            foreach (var p in partitionPayloads)
            {
                p.CopyTo(decrypted, pos);
                pos += p.Length;
            }

            partitionEncrypted = EncryptPartition(spec, partition, decrypted);
        }

        // --- Assemble the original ISO from the payloads -----------------------------
        var iso = new byte[isoSize];
        discHeader.CopyTo(iso, 0);
        var isoPos = 0x80L;
        var rawChunkIndex = 0;
        for (var i = 0; i < rawEntryChunkCounts[0].ChunkCount; i++, rawChunkIndex++)
        {
            var payload = payloads[rawChunkIndex];
            // Only the first raw chunk contains the disc header bytes at its start.
            var copyFrom = i == 0 ? Math.Min(0x80L, payload.Length) : 0;
            if (copyFrom > 0)
            {
                payload.AsSpan(0, (int)copyFrom).CopyTo(iso.AsSpan(0, (int)copyFrom));
            }

            payload.AsSpan((int)copyFrom).CopyTo(iso.AsSpan((int)isoPos));
            isoPos += payload.Length - copyFrom;
        }

        if (partition != null)
        {
            partitionEncrypted.CopyTo(iso, partitionOffset);
            isoPos = partitionOffset + partitionEncrypted.Length;
            var tailPayloadStart = rawEntryChunkCounts[0].ChunkCount + partitionPayloads.Count;
            for (var i = 0; i < rawEntryChunkCounts[1].ChunkCount; i++)
            {
                var payload = payloads[tailPayloadStart + i];
                payload.CopyTo(iso, isoPos);
                isoPos += payload.Length;
            }
        }

        var rvz = BuildFile(spec, discHeader, iso, payloads, rawEntryChunkCounts, partition, partitionOffset);
        return (rvz, iso);

        byte[] MakePayload(long chunkDiscOffset, int size)
        {
            var packed = spec.PackedChunks.Contains(globalChunk);
            var payload = new byte[size];
            if (packed)
            {
                var split = size / 2;
                var seedBytes = MakeSeed(spec.Seed ^ (int)chunkDiscOffset);
                var junk = ReferencePrng.Generate(seedBytes, chunkDiscOffset + split, size - split);
                for (var i = 0; i < split; i++)
                {
                    payload[i] = chunkDiscOffset + i < 0x80 && chunkDiscOffset == 0
                        ? discHeader[(int)(chunkDiscOffset + i)]
                        : RandomByte(rng, chunkDiscOffset + i, spec.Seed);
                }

                junk.CopyTo(payload, split);
            }
            else
            {
                for (var i = 0; i < size; i++)
                {
                    var discOffset = chunkDiscOffset + i;
                    payload[i] = discOffset < 0x80 && chunkDiscOffset == 0
                        ? discHeader[discOffset]
                        : RandomByte(rng, discOffset, spec.Seed);
                }
            }

            return payload;
        }

        void AddRawEntry(long start, long size, int entryIndex)
        {
            var alignedStart = start - start % 0x8000;
            var alignedSize = size + start % 0x8000;
            var count = 0;
            for (var offset = 0L; offset < alignedSize; offset += chunkSize)
            {
                var chunkSizeHere = (int)Math.Min(chunkSize, alignedSize - offset);
                payloads.Add(MakePayload(alignedStart + offset, chunkSizeHere));
                count++;
                globalChunk++;
            }

            rawEntryChunkCounts.Add((entryIndex, count));
        }
    }

    private static byte[] BuildFile(RvzSpec spec, byte[] discHeader, byte[] iso,
        List<byte[]> payloads, List<(int EntryIndex, int ChunkCount)> rawEntryChunkCounts,
        PartitionSpec? partition, long partitionOffset)
    {
        var compression = spec.Compression;
        var chunkSize = (long)spec.ChunkSize;
        var sectorsPerChunk = chunkSize / 0x8000;
        var partitionPayloadPerChunk = sectorsPerChunk * 0x7C00;

        // --- Encode groups -----------------------------------------------------------
        var groupEntries = new List<RvzGroupEntry>();
        var groupData = new List<byte[]>();
        // Group order in the file: raw entry 0, partition chunks, raw tail entry.
        var rawEntryChunkStart = new int[rawEntryChunkCounts.Count];
        var partitionChunkStart = rawEntryChunkCounts[0].ChunkCount;
        rawEntryChunkStart[0] = 0;
        if (rawEntryChunkCounts.Count > 1)
        {
            var partitionChunkCount = partition == null
                ? 0
                : (partition.SectorCount * 0x7C00L + partitionPayloadPerChunk - 1) / partitionPayloadPerChunk;
            rawEntryChunkStart[1] = partitionChunkStart + (int)partitionChunkCount;
        }

        // Payload list order: raw entry 0 chunks, partition chunks, raw tail chunks.
        var partitionPayloadStart = rawEntryChunkCounts[0].ChunkCount;
        var partitionPayloadCount = partition == null
            ? 0
            : (int)((partition.SectorCount * 0x7C00L + partitionPayloadPerChunk - 1) / partitionPayloadPerChunk);
        var tailPayloadStart = partitionPayloadStart + partitionPayloadCount;

        for (var c = 0; c < rawEntryChunkCounts[0].ChunkCount; c++)
        {
            var payload = payloads[c];
            AddGroup(spec, groupEntries, groupData, payload, c * chunkSize, isPartition: false);
        }

        if (partition != null)
        {
            for (var c = 0; c < partitionPayloadCount; c++)
            {
                var payload = payloads[partitionPayloadStart + c];
                var partitionOffsetInData = c * partitionPayloadPerChunk;
                AddGroup(spec, groupEntries, groupData, payload, partitionOffsetInData, isPartition: true,
                    partition, partitionOffsetInData);
            }
        }

        if (rawEntryChunkCounts.Count > 1)
        {
            var tailDiscBase = partitionOffset + partition!.SectorCount * 0x8000L;
            for (var c = 0; c < rawEntryChunkCounts[1].ChunkCount; c++)
            {
                var payload = payloads[tailPayloadStart + c];
                AddGroup(spec, groupEntries, groupData, payload, tailDiscBase + c * chunkSize,
                    isPartition: false);
            }
        }


        return AssembleFile(spec, discHeader, iso, groupEntries, groupData,
            rawEntryChunkStart, partitionChunkStart, partition, partitionOffset, chunkSize);
    }

    private static void AddGroup(RvzSpec spec, List<RvzGroupEntry> groupEntries, List<byte[]> groupData,
        byte[] payload, long dataOffset, bool isPartition, PartitionSpec? partition = null,
        long partitionDataOffset = 0)
    {
        var usePacking = !spec.IsWia && spec.PackedChunks.Contains(groupEntries.Count);
        byte[] stored = payload;
        uint packedSize = 0;
        if (usePacking)
        {
            (stored, packedSize) = PackPayload(payload, dataOffset, spec.Seed ^ (int)dataOffset);
        }

        if (isPartition)
        {
            stored = PrependExceptionLists(stored, spec, partition!, partitionDataOffset);
        }

        // For PURGE the exception lists stay uncompressed in front of the segment stream,
        // and the SHA-1 trailer covers lists + segments (Dolphin: PurgeCompressor).
        byte[] compressed;
        if (spec.Compression == CompressionType.Purge)
        {
            var listsBytes = isPartition
                ? BuildExceptionListBytes(spec, partition!, partitionDataOffset)
                : [];
            compressed = [.. listsBytes, .. TestCompressor.CompressPurge(payload, listsBytes)];
        }
        else
        {
            compressed = spec.Compression == CompressionType.None
                ? stored
                : TestCompressor.Compress(spec.Compression, stored);
        }

        groupData.Add(compressed);
        groupEntries.Add(new RvzGroupEntry(0, 0, packedSize));
    }

    /// <summary>Builds the packed representation: literal part + a PRNG-generated padded part.</summary>
    private static (byte[] Stored, uint PackedSize) PackPayload(byte[] payload, long dataOffset, int seed)
    {
        var split = payload.Length / 2;
        var literal = payload.AsSpan(0, split).ToArray();
        var padded = payload.AsSpan(split).ToArray();

        var seedBytes = MakeSeed(seed);
        var junk = ReferencePrng.Generate(seedBytes, dataOffset + split, padded.Length);

        var stored = new byte[4 + literal.Length + 4 + 68 + junk.Length];
        var pos = 0;
        WriteBe32(stored, ref pos, (uint)literal.Length);
        literal.CopyTo(stored, pos);
        pos += literal.Length;
        WriteBe32(stored, ref pos, 0x8000_0000u | (uint)junk.Length);
        seedBytes.CopyTo(stored, pos);
        pos += 68;
        junk.CopyTo(stored, pos);

        return (stored, (uint)stored.Length);
    }

    private static byte[] BuildExceptionListBytes(RvzSpec spec,
        PartitionSpec partition, long partitionDataOffset)
    {
        // Every partition chunk starts with one exception list per 2 MiB region it covers,
        // keyed to the FULL chunk size: Dolphin writes exception_lists_per_chunk =
        // max(1, chunk_size / 2 MiB) lists even for the final partial chunk (regions beyond
        // the chunk's data get an empty list; a chunk without exceptions carries one empty
        // list per region). Chunks up to 2 MiB therefore carry exactly one list.
        var regionsPerChunk = Math.Max(1, (int)((long)spec.ChunkSize / 0x200000));
        var regionBase = (int)(partitionDataOffset / 0x1F0000);
        // The chunk's position within its 2 MiB region: which sectors it covers and how
        // much the stored (chunk-relative) offsets are shifted from region-relative ones.
        var chunkSectorStart = (int)((partitionDataOffset % 0x1F0000) / 0x7C00);
        var sectorsPerChunk = (int)(spec.ChunkSize / 0x8000);
        var shift = chunkSectorStart * 0x400;
        using var header = new MemoryStream();
        for (var r = 0; r < regionsPerChunk; r++)
        {
            var list = regionBase + r < partition.Exceptions.Length ? partition.Exceptions[regionBase + r] : [];
            // Only the exceptions for sectors covered by this chunk belong in its list
            // (Dolphin: per-chunk exception lists). Entries for other sectors would wrap
            // around the u16 offset when shifted.
            var chunkList = list
                .Where(e => (e.Offset >> 10) >= chunkSectorStart &&
                            (e.Offset >> 10) < chunkSectorStart + sectorsPerChunk)
                .Select(e => new HashExceptionEntry((ushort)(e.Offset - shift), e.Hash))
                .ToList();
            header.WriteByte((byte)(chunkList.Count >> 8));
            header.WriteByte((byte)chunkList.Count);
            for (var i = 0; i < chunkList.Count; i++)
            {
                var entry = chunkList[i];
                header.WriteByte((byte)(entry.Offset >> 8));
                header.WriteByte((byte)entry.Offset);
                header.Write(entry.Hash);
            }
        }

        var headerBytes = header.ToArray();
        if (spec.Compression is CompressionType.None or CompressionType.Purge)
        {
            var padding = (4 - (headerBytes.Length % 4)) % 4;
            if (padding > 0)
            {
                headerBytes = [.. headerBytes, .. new byte[padding]];
            }
        }

        return headerBytes;
    }

    private static byte[] PrependExceptionLists(byte[] stored, RvzSpec spec,
        PartitionSpec partition, long partitionDataOffset)
    {
        return [.. BuildExceptionListBytes(spec, partition, partitionDataOffset), .. stored];
    }


    private static byte[] BuildPartTable(RvzSpec spec, PartitionSpec? partition, int partitionChunkStart,
        long chunkSize)
    {
        if (partition == null)
        {
            return [];
        }

        var partTable = new byte[0x30];
        partition.Key.CopyTo(partTable, 0);
        var partitionOffset = AlignUp(0x80L + spec.RawSize, 0x8000);
        WriteBe32(partTable, 0x10, (uint)(partitionOffset / 0x8000));
        WriteBe32(partTable, 0x14, (uint)partition.SectorCount);
        WriteBe32(partTable, 0x18, (uint)partitionChunkStart);
        var payloadPerChunk = chunkSize * 0x7C00 / 0x8000;
        WriteBe32(partTable, 0x1C, (uint)((partition.SectorCount * 0x7C00L + payloadPerChunk - 1) / payloadPerChunk));
        return partTable;
    }

    private static byte[] BuildRawTable(RvzSpec spec, PartitionSpec? partition, long partitionOffset,
        long chunkSize, int[] rawEntryChunkStart, CompressionType compression)
    {
        var rawEntries = new List<(ulong Off, ulong Size, uint GroupIndex, uint NumGroups)>();
        var firstEntrySize = partition == null ? spec.RawSize : (int)(partitionOffset - 0x80);
        rawEntries.Add((0x80, (ulong)firstEntrySize, (uint)rawEntryChunkStart[0],
            (uint)RawEntryChunkCountFor(spec, 0, firstEntrySize)));
        if (partition != null)
        {
            var tailOffset = partitionOffset + partition.SectorCount * 0x8000L;
            rawEntries.Add(((ulong)tailOffset, (ulong)spec.RawTailSize, (uint)rawEntryChunkStart[1],
                (uint)RawEntryChunkCountFor(spec, 1, spec.RawTailSize)));
        }

        var rawTable = new byte[rawEntries.Count * 0x18];
        for (var i = 0; i < rawEntries.Count; i++)
        {
            var pos = i * 0x18;
            WriteBe64(rawTable, pos, rawEntries[i].Off);
            WriteBe64(rawTable, pos + 8, rawEntries[i].Size);
            WriteBe32(rawTable, pos + 16, rawEntries[i].GroupIndex);
            WriteBe32(rawTable, pos + 20, rawEntries[i].NumGroups);
        }

        return compression == CompressionType.None
            ? rawTable
            : TestCompressor.Compress(compression, rawTable);
    }

    private static byte[] AssembleFile(RvzSpec spec, byte[] discHeader, byte[] iso,
        List<RvzGroupEntry> groupEntries, List<byte[]> groupData,
        int[] rawEntryChunkStart, int partitionChunkStart, PartitionSpec? partition,
        long partitionOffset, long chunkSize)
    {
        var compression = spec.Compression;

        // Build the partition and raw tables first (their content is fixed).
        var partTable = BuildPartTable(spec, partition, partitionChunkStart, chunkSize);
        var rawTableStored = BuildRawTable(spec, partition, partitionOffset, chunkSize,
            rawEntryChunkStart, compression);

        // The group entries' data_off4 depend on the group table's (compressed) length —
        // iterate until the layout is stable. A layout fits as soon as the stored size is
        // <= the size its offsets assumed (the difference becomes dead padding); equality
        // is not guaranteed for bzip2-style compressors, whose size can cycle.
        var groupEntrySize = spec.IsWia ? WiaGroupEntry.Size : RvzGroupEntry.Size;
        byte[] groupTableStored = [];
        var layoutSize = 0;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var groupTable = new byte[groupEntries.Count * groupEntrySize];
            var groupDataStart = AlignUp(0x48L + WiaDisc.Size + partTable.Length +
                                         rawTableStored.Length + layoutSize, 4);
            var running = groupDataStart;
            for (var i = 0; i < groupEntries.Count; i++)
            {
                var g = groupEntries[i];
                var pos = i * groupEntrySize;
                WriteBe32(groupTable, pos, (uint)(running / 4));
                if (spec.IsWia)
                {
                    WriteBe32(groupTable, pos + 4, (uint)groupData[i].Length);
                }
                else
                {
                    WriteBe32(groupTable, pos + 4, (uint)groupData[i].Length | 0x80000000u);
                    WriteBe32(groupTable, pos + 8, g.RvzPackedSize);
                }

                running += AlignUp(groupData[i].Length, 4);
            }

            var next = compression == CompressionType.None
                ? groupTable
                : TestCompressor.Compress(compression, groupTable);
            if (next.Length <= layoutSize)
            {
                groupTableStored = next;
                break;
            }

            layoutSize = next.Length;
        }

        // Layout: file head | disc | part table | raw table | group table | group data
        const long partOff = 0x48L + WiaDisc.Size;
        var rawOff = partOff + partTable.Length;
        var groupOff = rawOff + rawTableStored.Length;

        var disc = BuildDisc(spec, discHeader, partTable, rawTableStored, groupTableStored,
            partOff, rawOff, groupOff, partition, groupEntries.Count);
        var discHash = SHA1.HashData(disc);

        // The file head hash covers everything up to (not including) file_head_hash, which
        // includes rvz_file_size — so the head must be written after the body size is known.
        var outStream = new MemoryStream();
        outStream.Write(disc);
        outStream.Write(partTable);
        outStream.Write(rawTableStored);
        outStream.Write(groupTableStored);
        // Pad to the aligned group-data start the table's offsets were computed from
        // (layoutSize may exceed the stored size; the difference is dead padding).
        var finalDataStart = AlignUp(0x48L + WiaDisc.Size + partTable.Length +
                                     rawTableStored.Length + layoutSize, 4);
        while (outStream.Position + 0x48 < finalDataStart)
        {
            outStream.WriteByte(0);
        }

        foreach (var data in groupData)
        {
            outStream.Write(data);
            while (outStream.Position % 4 != 0)
            {
                outStream.WriteByte(0); // group offsets are stored divided by 4
            }
        }

        var result = outStream.ToArray();
        var fileHead = new byte[WiaFileHead.Size];
        (spec.IsWia ? "WIA"u8 : "RVZ"u8).CopyTo(fileHead);
        WriteBe32(fileHead, 4, 0x01000000);
        WriteBe32(fileHead, 8, spec.IsWia ? 0x01000000u : 0x00030000u);
        WriteBe32(fileHead, 12, WiaDisc.Size);
        discHash.CopyTo(fileHead, 16);
        WriteBe64(fileHead, 36, (ulong)iso.Length);
        WriteBe64(fileHead, 44, (ulong)(WiaFileHead.Size + result.Length));
        var headHash = SHA1.HashData(fileHead.AsSpan(0, 0x34));
        headHash.CopyTo(fileHead, 0x34);

        var final = new byte[WiaFileHead.Size + result.Length];
        fileHead.CopyTo(final, 0);
        result.CopyTo(final, WiaFileHead.Size);
        return final;
    }

    private static int RawEntryChunkCountFor(RvzSpec spec, int entryIndex, int entrySize)
    {
        var chunkSize = (long)spec.ChunkSize;
        var aligned = entryIndex == 0
            ? entrySize + 0x80L // 0x80 % 0x8000 == 0x80
            : entrySize;
        return (int)((aligned + chunkSize - 1) / chunkSize);
    }

    private static byte[] BuildDisc(RvzSpec spec, byte[] discHeader, byte[] partTable,
        byte[] rawTableStored, byte[] groupTableStored, long partOff, long rawOff, long groupOff,
        PartitionSpec? partition, int groupCount)
    {
        var disc = new byte[WiaDisc.Size];
        WriteBe32(disc, 0, (uint)spec.DiscType);
        WriteBe32(disc, 4, (uint)spec.Compression);
        WriteBe32(disc, 8, 3);
        WriteBe32(disc, 12, spec.ChunkSize);
        discHeader.CopyTo(disc, 16);

        WriteBe32(disc, 0x90, (uint)(partition == null ? 0 : 1)); // n_part
        WriteBe32(disc, 0x94, 0x30); // part_t_size
        WriteBe64(disc, 0x98, (ulong)partOff);
        // SHA-1 of the (possibly empty) partition table — the reader verifies it either way.
        SHA1.HashData(partTable).CopyTo(disc, 0xA0);

        WriteBe32(disc, 0xB4, (uint)(partition == null ? 1 : 2)); // n_raw_data
        WriteBe64(disc, 0xB8, (ulong)rawOff);
        WriteBe32(disc, 0xC0, (uint)rawTableStored.Length);

        WriteBe32(disc, 0xC4, (uint)groupCount);
        WriteBe64(disc, 0xC8, (ulong)groupOff);
        WriteBe32(disc, 0xD0, (uint)groupTableStored.Length);

        var (comprDataLen, comprData) = MakeComprData(spec.Compression);
        disc[0xD4] = comprDataLen;
        comprData.CopyTo(disc, 0xD5);
        return disc;
    }

    private static (byte Len, byte[] Data) MakeComprData(CompressionType compression)
    {
        switch (compression)
        {
            case CompressionType.None:
            case CompressionType.Bzip2:
            case CompressionType.Zstd:
                return (0, new byte[7]);
            case CompressionType.Lzma:
            {
                var (props, _) = TestCompressor.EncodeLzma1([1], endMarker: true);
                var data = new byte[7];
                props.CopyTo(data, 0);
                return ((byte)props.Length, data);
            }
            case CompressionType.Lzma2:
                return (1, [21, 0, 0, 0, 0, 0, 0]);
            case CompressionType.Purge:
                return (0, new byte[7]);
            default:
                throw new ArgumentOutOfRangeException(nameof(compression));
        }
    }

    private static byte[] EncryptPartition(RvzSpec spec, PartitionSpec partition, byte[] decrypted)
    {
        // One region builder per 64 sectors (regions hash zero-filled tails independently).
        var output = new MemoryStream();
        for (var regionStart = 0; regionStart < partition.SectorCount; regionStart += 64)
        {
            var regionEnd = Math.Min(regionStart + 64, partition.SectorCount);
            var builder = new PartitionRegionBuilder(partition.Key);
            var region = regionStart / 64;
            for (var s = regionStart; s < regionEnd; s++)
            {
                var exceptions = new List<HashExceptionEntry>();
                if (region < partition.Exceptions.Length)
                {
                    foreach (var ex in partition.Exceptions[region])
                    {
                        if (ex.Offset >> 10 == s - regionStart)
                        {
                            exceptions.Add(new HashExceptionEntry((ushort)(ex.Offset & 0x3FF), ex.Hash));
                        }
                    }
                }

                builder.AddSector(decrypted.AsSpan(s * 0x7C00, 0x7C00),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(exceptions));
            }

            output.Write(builder.Finish());
        }

        return output.ToArray();
    }

    private static byte[] MakeSeed(int seed)
    {
        var seedBytes = new byte[68];
        new Random(seed).NextBytes(seedBytes);
        return seedBytes;
    }

    private static byte RandomByte(Random rng, long discOffset, int seed)
    {
        unchecked
        {
            return (byte)((discOffset * 2654435761L + seed * 40503L + (discOffset >> 3)) & 0xFF);
        }
    }

    private static long AlignUp(long value, long alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }

    private static void WriteBe32(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    private static void WriteBe32(byte[] b, ref int offset, uint value)
    {
        WriteBe32(b, offset, value);
        offset += 4;
    }

    private static void WriteBe64(byte[] b, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            b[offset + i] = (byte)(value >> (56 - 8 * i));
        }
    }
}
