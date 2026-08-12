using RVZSharp.Chunks;
using RVZSharp.Compression;
using RVZSharp.Format;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Reads and decodes an RVZ disc image. Parses and validates the full container at
/// <see cref="Open"/>, then serves the original disc image (ISO) bytes via
/// <see cref="ReadAt"/> — byte-identical to the source disc, including re-encrypted Wii
/// partition data and rebuilt hash trees.
/// </summary>
public sealed class RvzReader : IDisposable
{
    /// <summary>Bytes of partition data per 2 MiB region (64 sectors × 0x7C00).</summary>
    public const int RegionDataSize = 64 * WiiHashCalculator.SectorDataSize; // 0x1F0000

    private readonly Stream _file;
    private readonly bool _leaveOpen;
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
        Partition,
    }

    private readonly record struct DataArea(long Start, long End, AreaKind Kind, int Index, int Segment);

    private RvzReader(Stream file, bool leaveOpen, WiaFileHead fileHead, WiaDisc disc,
        WiaPartEntry[] partitions, WiaRawDataEntry[] rawData, RvzGroupEntry[] groups)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        FileHead = fileHead;
        Disc = disc;
        Partitions = partitions;
        RawDataEntries = rawData;
        GroupEntries = groups;
        _codec = CompressionCodecFactory.Create(disc.Compression);
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

    public WiaFileHead FileHead { get; }
    public WiaDisc Disc { get; }
    public WiaPartEntry[] Partitions { get; }
    public WiaRawDataEntry[] RawDataEntries { get; }
    public RvzGroupEntry[] GroupEntries { get; }

    /// <summary>Size of the original disc image in bytes.</summary>
    public long Length { get; }

    /// <summary>Parses and validates an RVZ file. The stream must be seekable.</summary>
    public static RvzReader Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The RVZ stream must be seekable.", nameof(stream));
        }

        var headBytes = new byte[WiaFileHead.Size];
        if (!ReadExactlyAt(stream, 0, headBytes))
        {
            throw new RvzFormatException("The file is too short to contain an RVZ file head.");
        }

        var fileHead = WiaFileHead.Parse(headBytes);
        fileHead.Validate(headBytes, stream.Length);

        var discBytes = new byte[fileHead.DiscSize];
        if (!ReadExactlyAt(stream, WiaFileHead.Size, discBytes))
        {
            throw new RvzFormatException("The file is too short to contain the disc struct.");
        }

        var disc = WiaDisc.Parse(discBytes);
        disc.Validate(fileHead.DiscSize, discBytes, fileHead.DiscHash);

        var partitions = TableParser.ParsePartitions(stream, disc);
        var rawData = TableParser.ParseRawDataEntries(stream, disc);
        var groups = TableParser.ParseGroupEntries(stream, disc);

        return new RvzReader(stream, leaveOpen, fileHead, disc, partitions, rawData, groups);
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

    /// <summary>Decodes the whole disc image into a single buffer.</summary>
    public byte[] ReadFully()
    {
        var output = new byte[Length];
        var position = 0L;
        while (position < Length)
        {
            var read = ReadAt(position, output.AsSpan((int)position));
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
        var regionBytes = 64L * PartitionRegionBuilder.SectorSize;
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
        var key = (long)rawIndex << 32 | chunkIndex;
        if (_cachedRawKey != key || _cachedRawPayload == null)
        {
            var entry = RawDataEntries[rawIndex];
            var group = GroupEntries[entry.GroupIndex + chunkIndex];
            var expectedSize = (int)Math.Min(Disc.ChunkSize, areaSize - chunkIndex * Disc.ChunkSize);
            var result = ChunkDecoder.DecodeChunk(_file, Disc, _codec,
                new ChunkDecodeRequest
                {
                    Group = group,
                    IsPartition = false,
                    ExpectedSize = expectedSize,
                    DataOffset = chunkIndex * Disc.ChunkSize,
                });
            _cachedRawPayload = result.Payload;
            _cachedRawKey = key;
        }

        return _cachedRawPayload;
    }

    private void ReadPartitionArea(DataArea area, long position, Span<byte> buffer)
    {
        var regionBytes = 64L * PartitionRegionBuilder.SectorSize;
        var offsetInArea = position - area.Start;
        var regionIndex = offsetInArea / regionBytes;
        var offsetInRegion = (int)(offsetInArea % regionBytes);

        var region = GetPartitionRegion(area, regionIndex);
        region.AsSpan(offsetInRegion, buffer.Length).CopyTo(buffer);
    }

    private byte[] GetPartitionRegion(DataArea area, long regionIndex)
    {
        var key = (long)area.Index << 32 | regionIndex;
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
            var remainingSectors = (long)pd.NumSectors - chunkIndex * sectorsPerChunk;
            var expectedSize = (int)(Math.Min(sectorsPerChunk, remainingSectors) * WiiHashCalculator.SectorDataSize);

            var group = GroupEntries[pd.GroupIndex + chunkIndex];
            var result = ChunkDecoder.DecodeChunk(_file, Disc, _codec,
                new ChunkDecodeRequest
                {
                    Group = group,
                    IsPartition = true,
                    ExpectedSize = expectedSize,
                    DataOffset = chunkIndex * PartitionChunkPayloadSize,
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

        // Chunks start at 2 MiB region boundaries, so list offsets are region-relative.
        // Each entry names a sector (offset >> 10) and a position within its hash area.
        return lists[listIndex]
            .Where(e => (e.Offset >> 10) == (int)(sector % 64))
            .Select(e => new HashExceptionEntry((ushort)(e.Offset & 0x3FF), e.Hash))
            .ToArray();
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

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _file.Dispose();
        }
    }
}
