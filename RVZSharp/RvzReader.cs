using System.Runtime.InteropServices;
using RVZSharp.Interfaces;
using RVZSharp.Chunks;
using RVZSharp.Compression;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Reads and decodes an RVZ or WIA disc image. Parses and validates the full container at
/// <see cref="Open(Stream, bool)"/> (RVZ) or <see cref="OpenWia(Stream, bool)"/> (WIA), then
/// serves the original disc image (ISO) bytes via <see cref="ReadAt"/> — byte-identical to the
/// source disc, including re-encrypted Wii partition data and rebuilt hash trees.
/// </summary>
public sealed class RvzReader : IBlobReader
{
    /// <summary>Bytes of partition data per 2 MiB region (64 sectors × 0x7C00).</summary>
    public const int RegionDataSize = 64 * WiiHashCalculator.SectorDataSize; // 0x1F0000

    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private readonly WiaRvzFormat _format;
    private readonly ICompressionDecoder _codec;
    private readonly DataArea[] _areas;

    private byte[]? _cachedRawPayload;
    private long _cachedRawKey = -1;
    private byte[]? _cachedRegion;
    private long _cachedRegionKey = -1;
    private byte[]? _cachedChunkPayload;
    private HashExceptionEntry[][] _cachedChunkLists = [];
    private long _cachedChunkKey = -1;

    private enum AreaKind
    {
        Raw,
        Partition
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DataArea(long Start, long End, AreaKind Kind, int Index, int Segment);

    private RvzReader(Stream file, bool leaveOpen, WiaRvzFormat format, WiaFileHead fileHead,
        WiaDisc disc, WiaPartEntry[] partitions, WiaRawDataEntry[] rawData, GroupEntry[] groups)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        _format = format;
        FileHead = fileHead;
        Disc = disc;
        Partitions = partitions;
        RawDataEntries = rawData;
        GroupEntries = groups;
        _codec = disc.Compression == CompressionType.Purge
            ? null! // PURGE has no streaming codec; ChunkDecoder handles it directly (WIA only)
            : CompressionCodecFactory.Create(disc.Compression);
        Length = (long)fileHead.IsoFileSize;

        var areas = new List<DataArea>();
        for (var i = 0; i < rawData.Length; i++)
        {
            var raw = rawData[i];
            areas.Add(new DataArea((long)raw.RawDataOffset, (long)(raw.RawDataOffset + raw.RawDataSize),
                AreaKind.Raw, i, 0));
        }

        for (var p = 0; p < partitions.Length; p++)
        {
            for (var segment = 0; segment < 2; segment++)
            {
                var pd = partitions[p].Data[segment];
                if (pd.NumSectors == 0)
                {
                    continue;
                }

                var start = (long)pd.FirstSector * WiaDisc.SectorSize;
                areas.Add(new DataArea(start, start + (long)pd.NumSectors * WiaDisc.SectorSize,
                    AreaKind.Partition, p, segment));
            }
        }

        _areas = areas.OrderBy(a => a.Start).ToArray();
    }

    /// <summary>The parsed and validated file head (magic, format version, sizes, hashes).</summary>
    public WiaFileHead FileHead { get; }

    /// <summary>The parsed disc struct (disc type, compression method and level, chunk size, table offsets).</summary>
    public WiaDisc Disc { get; }

    /// <summary>The partition table entries, each with up to two data segments and their group ranges.</summary>
    public WiaPartEntry[] Partitions { get; }

    /// <summary>The raw data table entries, each covering a contiguous byte range of the disc.</summary>
    public WiaRawDataEntry[] RawDataEntries { get; }

    /// <summary>The group table entries in file order, describing every stored chunk.</summary>
    public GroupEntry[] GroupEntries { get; }

    /// <summary>True when this reader decodes a WIA file; false for RVZ.</summary>
    public bool IsWia => _format == WiaRvzFormat.Wia;

    /// <summary>The blob format of the underlying file.</summary>
    public BlobType Type => IsWia ? BlobType.Wia : BlobType.Rvz;

    /// <summary>Chunk size of the file (0 for formats that do not use blocks).</summary>
    public int BlockSize => (int)Disc.ChunkSize;

    /// <summary>Size of the original disc image in bytes.</summary>
    public long Length { get; }

    /// <summary>Parses and validates an RVZ file. The stream must be seekable.</summary>
    public static RvzReader Open(Stream stream, bool leaveOpen = false)
    {
        return Open(stream, leaveOpen, WiaRvzFormat.Rvz);
    }

    /// <summary>Parses and validates a WIA file. The stream must be seekable.</summary>
    public static RvzReader OpenWia(Stream stream, bool leaveOpen = false)
    {
        return Open(stream, leaveOpen, WiaRvzFormat.Wia);
    }

    private static RvzReader Open(Stream stream, bool leaveOpen, WiaRvzFormat format)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException(
                $"The {(format == WiaRvzFormat.Wia ? "WIA" : "RVZ")} stream must be seekable.",
                nameof(stream));
        }

        var headBytes = new byte[WiaFileHead.Size];
        if (!ReadExactlyAt(stream, 0, headBytes))
        {
            throw new RvzFormatException(
                "The file is too short to contain a WIA/RVZ file head.");
        }

        var fileHead = WiaFileHead.Parse(headBytes);
        fileHead.Validate(headBytes, stream.Length, format);

        var discBytes = new byte[fileHead.DiscSize];
        if (!ReadExactlyAt(stream, WiaFileHead.Size, discBytes))
        {
            throw new RvzFormatException("The file is too short to contain the disc struct.");
        }

        var disc = WiaDisc.Parse(discBytes);
        disc.Validate(fileHead.DiscSize, discBytes, fileHead.DiscHash, format);

        var partitions = TableParser.ParsePartitions(stream, disc);
        var rawData = TableParser.ParseRawDataEntries(stream, disc);
        var groups = TableParser.ParseGroupEntries(stream, disc, format);
        ValidateDataLayout(partitions, rawData);

        return new RvzReader(stream, leaveOpen, format, fileHead, disc, partitions, rawData, groups);
    }

    /// <summary>
    /// Rejects tables whose data areas overlap or are misordered, mirroring Dolphin's
    /// partition-segment ordering check (WIABlob.cpp:204-208) and HasDataOverlap
    /// (WIABlob.cpp:244-277): every non-empty data area must be covered by its own
    /// end-keyed entry (first_sector × 0x8000 for partition data, raw offsets as-is).
    /// </summary>
    private static void ValidateDataLayout(WiaPartEntry[] partitions, WiaRawDataEntry[] rawData)
    {
        const long blockSize = WiaDisc.SectorSize; // 0x8000: the partition entry sector unit

        // The two segments of a partition must be in order (segment 0 before segment 1).
        foreach (var partition in partitions)
        {
            if (partition.Data[0].NumSectors != 0 && partition.Data[1].NumSectors != 0 &&
                partition.Data[0].FirstSector > partition.Data[1].FirstSector)
            {
                throw new RvzFormatException(
                    "The partition table contains a data entry whose segments are out of order.");
            }
        }

        // End-keyed map of every non-empty data area (std::map::emplace: first wins).
        var ends = new SortedDictionary<long, (bool IsPartition, int Index, int Segment)>();

        for (var i = 0; i < partitions.Length; i++)
        {
            for (var segment = 0; segment < 2; segment++)
            {
                var entry = partitions[i].Data[segment];
                if (entry.NumSectors != 0)
                {
                    AddEnd(((long)entry.FirstSector + entry.NumSectors) * blockSize, true, i, segment);
                }
            }
        }

        for (var i = 0; i < rawData.Length; i++)
        {
            if (rawData[i].RawDataSize != 0)
            {
                AddEnd((long)(rawData[i].RawDataOffset + rawData[i].RawDataSize), false, i, 0);
            }
        }

        for (var i = 0; i < partitions.Length; i++)
        {
            for (var segment = 0; segment < 2; segment++)
            {
                var entry = partitions[i].Data[segment];
                if (entry.NumSectors != 0 &&
                    !Covered(entry.FirstSector * blockSize, true, i, segment))
                {
                    throw new RvzFormatException(
                        "The disc tables contain overlapping or misplaced partition data.");
                }
            }
        }

        for (var i = 0; i < rawData.Length; i++)
        {
            if (rawData[i].RawDataSize != 0 &&
                !Covered((long)rawData[i].RawDataOffset, false, i, 0))
            {
                throw new RvzFormatException(
                    "The disc tables contain overlapping or misplaced raw data.");
            }
        }

        return;

        void AddEnd(long end, bool isPartition, int index, int segment)
        {
            if (!ends.ContainsKey(end))
            {
                ends[end] = (isPartition, index, segment);
            }
        }

        // Each area's start must be covered by exactly its own end-keyed entry (Dolphin:
        // upper_bound(start) must find the entry itself — anything else is an overlap or
        // a gap/ordering error).
        bool Covered(long start, bool isPartition, int index, int segment)
        {
            foreach (var pair in ends)
            {
                if (pair.Key > start)
                {
                    return pair.Value == (isPartition, index, segment);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Reads <paramref name="buffer.Length"/> bytes of the decoded disc image at
    /// <paramref name="position"/>. Returns fewer bytes at the end of the image.
    /// </summary>
    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var total = 0;

        // The first 0x80 bytes of the image are served from the disc header (dhead).
        if (position < WiaDisc.DiscHeaderSize)
        {
            var take = (int)Math.Min(buffer.Length, WiaDisc.DiscHeaderSize - position);
            Disc.DiscHeader.AsSpan((int)position, take).CopyTo(buffer);
            position += take;
            total += take;
            buffer = buffer[take..];
        }

        while (!buffer.IsEmpty)
        {
            if (position >= Length)
            {
                break; // the caller asked for more bytes than the image holds
            }

            var area = FindArea(position)
                       ?? throw new RvzFormatException(
                           $"No data covers disc offset 0x{position:X}; the file is not a complete disc image.");

            // Clamp to the current chunk (raw) or 64-sector region (partition).
            var take = area.Kind == AreaKind.Raw
                ? ClampToRawChunk(area, position, buffer.Length)
                : ClampToRegion(area, position, buffer.Length);
            if (area.Kind == AreaKind.Raw)
            {
                ReadRawArea(area, position, buffer[..take]);
            }
            else
            {
                ReadPartitionArea(area, position, buffer[..take]);
            }

            position += take;
            total += take;
            buffer = buffer[take..];
        }

        return total;
    }

    /// <summary>
    /// Decodes the whole disc image into a single buffer. The image must fit in memory
    /// (byte arrays are capped at <see cref="int.MaxValue"/> elements, so this supports
    /// discs up to 2 GiB — use <see cref="ReadAt"/> for larger images).
    /// </summary>
    public byte[] ReadFully()
    {
        var output = new byte[Length];
        var position = 0L;
        while (position < Length)
        {
            // Copy in bounded pieces so the span offsets stay within int range.
            var take = (int)Math.Min(1 << 20, Length - position);
            var read = ReadAt(position, output.AsSpan((int)position, take));
            if (read <= 0)
            {
                throw new RvzFormatException($"Read stopped at offset 0x{position:X}.");
            }

            position += read;
        }

        return output;
    }

    private int ClampToRawChunk(DataArea area, long position, int requested)
    {
        var chunkSize = (long)Disc.ChunkSize;
        var chunkStart = area.Start + (position - area.Start) / chunkSize * chunkSize;
        var chunkEnd = Math.Min(area.End, chunkStart + chunkSize);
        return (int)Math.Min(requested, chunkEnd - position);
    }

    private static int ClampToRegion(DataArea area, long position, int requested)
    {
        const long regionBytes = 64L * PartitionRegionBuilder.SectorSize;
        var regionStart = area.Start + (position - area.Start) / regionBytes * regionBytes;
        var regionEnd = Math.Min(area.End, regionStart + regionBytes);
        return (int)Math.Min(requested, regionEnd - position);
    }

    private void ReadRawArea(DataArea area, long position, Span<byte> buffer)
    {
        var chunkSize = (long)Disc.ChunkSize;
        var areaSize = area.End - area.Start;
        var chunkIndex = (position - area.Start) / chunkSize;
        var offsetInChunk = (int)((position - area.Start) % chunkSize);

        var payload = GetRawChunk(area.Index, chunkIndex, areaSize);
        payload.AsSpan(offsetInChunk, buffer.Length).CopyTo(buffer);
    }

    private byte[] GetRawChunk(int rawIndex, long chunkIndex, long areaSize)
    {
        var key = ((long)rawIndex << 32) | chunkIndex;
        if (_cachedRawKey != key || _cachedRawPayload == null)
        {
            var entry = RawDataEntries[rawIndex];
            var groupIndex = entry.GroupIndex + chunkIndex;
            if (groupIndex >= GroupEntries.Length)
            {
                throw new RvzFormatException(
                    $"Raw-data entry {rawIndex} references group {groupIndex}, but only "
                    + $"{GroupEntries.Length} groups exist.");
            }

            var group = GroupEntries[groupIndex];
            var expectedSize = (int)Math.Min(Disc.ChunkSize, areaSize - chunkIndex * Disc.ChunkSize);
            var result = ChunkDecoder.DecodeChunk(_file, Disc, _codec,
                new ChunkDecodeRequest
                {
                    Group = group,
                    IsPartition = false,
                    ExpectedSize = expectedSize,
                    DataOffset = chunkIndex * Disc.ChunkSize
                });
            _cachedRawPayload = result.Payload;
            _cachedRawKey = key;
        }

        return _cachedRawPayload;
    }

    private void ReadPartitionArea(DataArea area, long position, Span<byte> buffer)
    {
        const long regionBytes = 64L * PartitionRegionBuilder.SectorSize;
        var offsetInArea = position - area.Start;
        var regionIndex = offsetInArea / regionBytes;
        var offsetInRegion = (int)(offsetInArea % regionBytes);

        var region = GetPartitionRegion(area, regionIndex);
        region.AsSpan(offsetInRegion, buffer.Length).CopyTo(buffer);
    }

    private byte[] GetPartitionRegion(DataArea area, long regionIndex)
    {
        var key = ((long)area.Index << 40) | ((long)area.Segment << 32) | regionIndex;
        if (_cachedRegionKey != key || _cachedRegion == null)
        {
            _cachedRegion = BuildPartitionRegion(area, regionIndex);
            _cachedRegionKey = key;
        }

        return _cachedRegion;
    }

    private byte[] BuildPartitionRegion(DataArea area, long regionIndex)
    {
        var part = Partitions[area.Index];
        var pd = part.Data[area.Segment];
        var sectorsInArea = (long)pd.NumSectors;
        var sectorsPerChunk = Disc.ChunkSize / WiaDisc.SectorSize;
        var regionStartSector = regionIndex * 64;
        var regionEndSector = Math.Min(regionStartSector + 64, sectorsInArea);

        var builder = new PartitionRegionBuilder(part.Key);
        for (var sector = regionStartSector; sector < regionEndSector; sector++)
        {
            var chunkIndex = sector / sectorsPerChunk;
            var sectorInChunk = (int)(sector % sectorsPerChunk);
            var (payload, lists) = GetPartitionChunk(area, chunkIndex);
            var sectorData = payload.AsSpan(sectorInChunk * WiiHashCalculator.SectorDataSize,
                WiiHashCalculator.SectorDataSize);

            builder.AddSector(sectorData, GetSectorExceptions(chunkIndex, regionIndex, sector, lists));
        }

        return builder.Finish();
    }

    private (byte[] Payload, HashExceptionEntry[][] Lists) GetPartitionChunk(DataArea area, long chunkIndex)
    {
        var key = ((long)area.Index << 40) | ((long)area.Segment << 32) | chunkIndex;
        if (_cachedChunkKey != key || _cachedChunkPayload == null)
        {
            var pd = Partitions[area.Index].Data[area.Segment];
            var sectorsPerChunk = Disc.ChunkSize / WiaDisc.SectorSize;
            var remainingSectors = pd.NumSectors - chunkIndex * sectorsPerChunk;
            var expectedSize = (int)(Math.Min(sectorsPerChunk, remainingSectors) * WiiHashCalculator.SectorDataSize);

            var groupIndex = pd.GroupIndex + chunkIndex;
            if (groupIndex >= GroupEntries.Length)
            {
                throw new RvzFormatException(
                    $"Partition data entry references group {groupIndex}, but only "
                    + $"{GroupEntries.Length} groups exist.");
            }

            var group = GroupEntries[groupIndex];
            var result = ChunkDecoder.DecodeChunk(_file, Disc, _codec,
                new ChunkDecodeRequest
                {
                    Group = group,
                    IsPartition = true,
                    ExpectedSize = expectedSize,
                    DataOffset = chunkIndex * PartitionChunkPayloadSize
                });
            _cachedChunkPayload = result.Payload;
            _cachedChunkLists = result.ExceptionLists;
            _cachedChunkKey = key;
        }

        return (_cachedChunkPayload, _cachedChunkLists);
    }

    private HashExceptionEntry[] GetSectorExceptions(long chunkIndex, long regionIndex,
        long sector, HashExceptionEntry[][] lists)
    {
        var chunkRegionBase = chunkIndex * PartitionChunkPayloadSize / RegionDataSize;
        var listIndex = (int)(regionIndex - chunkRegionBase);
        if (listIndex < 0 || listIndex >= lists.Length)
        {
            return [];
        }

        // The writer stores exception offsets relative to the chunk; the chunk's position
        // within its 2 MiB region shifts them to region-relative (Dolphin: additional_offset).
        var additionalOffset = (int)((chunkIndex * PartitionChunkPayloadSize % RegionDataSize) /
            WiiHashCalculator.SectorDataSize * WiiHashCalculator.HashBlockSize);

        // Each entry names a sector (offset >> 10) and a position within its hash area.
        // Out-of-range entries are rejected, not silently dropped (Dolphin:
        // ApplyHashExceptions, WIABlob.cpp:868-876).
        var exceptions = new List<HashExceptionEntry>();
        foreach (var entry in lists[listIndex])
        {
            var regionOffset = entry.Offset + additionalOffset;
            var blockIndex = regionOffset >> 10;
            var offsetInBlock = regionOffset & 0x3FF;
            if (blockIndex >= 64 ||
                offsetInBlock + WiiHashCalculator.HashSize > WiiHashCalculator.HashBlockSize)
            {
                throw new RvzFormatException(
                    $"Hash exception at offset 0x{entry.Offset:X4} is outside the region's hash area.");
            }

            if (blockIndex == (int)(sector % 64))
            {
                exceptions.Add(new HashExceptionEntry((ushort)offsetInBlock, entry.Hash));
            }
        }

        return exceptions.ToArray();
    }

    private int PartitionChunkPayloadSize =>
        (int)((long)Disc.ChunkSize * WiiHashCalculator.SectorDataSize / WiaDisc.SectorSize);

    private DataArea? FindArea(long offset)
    {
        DataArea? candidate = null;
        foreach (var area in _areas)
        {
            if (area.Start > offset)
            {
                break;
            }

            if (offset < area.End)
            {
                candidate = area;
            }
        }

        return candidate;
    }

    private static bool ReadExactlyAt(Stream stream, long position, Span<byte> buffer)
    {
        if (stream.Position != position)
        {
            stream.Position = position;
        }

        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    /// <summary>Disposes the underlying stream, unless it was opened with leaveOpen set.</summary>
    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _file.Dispose();
        }
    }
}
