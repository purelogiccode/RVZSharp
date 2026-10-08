using System.Buffers;
using System.Runtime.InteropServices;
using RVZSharp.Interfaces;
using RVZSharp.Chunks;
using RVZSharp.Compression;
using RVZSharp.IO;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp;

/// <summary>
/// Reads and decodes an RVZ or WIA disc image. Parses and validates the full container at
/// <see cref="Open(Stream, bool)"/> (RVZ) or <see cref="OpenWia(Stream, bool)"/> (WIA), then
/// serves the original disc image (ISO) bytes via <see cref="ReadAt"/> — byte-identical to the
/// source disc, including re-encrypted Wii partition data and rebuilt hash trees.
/// <see cref="ReadAt"/> is thread-safe: decoded units are shared through a bounded LRU cache
/// (<see cref="DefaultCacheSize"/>), so concurrent random access is supported. Do not dispose
/// the reader while another thread is reading.
/// </summary>
public sealed class RvzReader : IBlobReader
{
    /// <summary>Bytes of partition data per 2 MiB region (64 sectors × 0x7C00).</summary>
    public const int RegionDataSize = 64 * WiiHashCalculator.SectorDataSize; // 0x1F0000

    /// <summary>Bytes of decoded data cached for random access (Dolphin: CachedBlob).</summary>
    public const int DefaultCacheSize = 16 * 1024 * 1024;

    private const byte KindRaw = 0;
    private const byte KindPartitionChunk = 1;
    private const byte KindPartitionRegion = 2;

    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private readonly WiaRvzFormat _format;
    private readonly ICompressionDecoder _codec;
    private readonly DataArea[] _areas;

    /// <summary>
    /// Thread-safe LRU of decoded units (raw chunks, partition chunks and rebuilt partition
    /// regions) so concurrent <see cref="ReadAt"/> calls share decoded data.
    /// </summary>
    private readonly LruCache<CacheKey, CacheValue> _cache = new(DefaultCacheSize);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CacheKey(byte Kind, int Area, int Segment, long Unit);

    private sealed class CacheValue
    {
        public required byte[] Payload { get; init; }
        public HashExceptionEntry[][] Lists { get; init; } = [];
    }

    /// <summary>Serializes file reads; decompression itself runs lock-free on worker threads.</summary>
#if NET9_0_OR_GREATER
    private readonly Lock _fileLock = new();
#else
    private readonly object _fileLock = new();
#endif

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

    /// <summary>
    /// Parses and validates an RVZ file by path. The returned reader owns the file stream;
    /// disposing the reader closes it.
    /// </summary>
    /// <param name="path">Path of the RVZ file.</param>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="RvzFormatException">The file is structurally invalid.</exception>
    /// <exception cref="RvzHashMismatchException">A container SHA-1 does not match.</exception>
    /// <exception cref="RvzUnsupportedException">The file version is unsupported.</exception>
    public static RvzReader Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            return Open(stream, leaveOpen: false, WiaRvzFormat.Rvz);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses and validates a WIA file by path. The returned reader owns the file stream;
    /// disposing the reader closes it.
    /// </summary>
    /// <param name="path">Path of the WIA file.</param>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="RvzFormatException">The file is structurally invalid.</exception>
    /// <exception cref="RvzHashMismatchException">A container SHA-1 does not match.</exception>
    /// <exception cref="RvzUnsupportedException">The file version is unsupported.</exception>
    public static RvzReader OpenWia(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            return Open(stream, leaveOpen: false, WiaRvzFormat.Wia);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
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
        ValidateTableBounds(stream, fileHead, disc);

        var partitions = TableParser.ParsePartitions(stream, disc);
        var rawData = TableParser.ParseRawDataEntries(stream, disc);
        var groups = TableParser.ParseGroupEntries(stream, disc, format);
        ValidateDataLayout(partitions, rawData);

        return new RvzReader(stream, leaveOpen, format, fileHead, disc, partitions, rawData, groups);
    }

    /// <summary>
    /// Bounds the table counts and offsets before any table allocation. Every group and raw
    /// entry covers at least one 32 KiB chunk of the declared image, so counts must be
    /// consistent with <c>iso_file_size</c>; absolute caps keep hostile headers from
    /// requesting multi-gigabyte allocations (the table hashes are checked only afterwards).
    /// </summary>
    private static void ValidateTableBounds(Stream stream, WiaFileHead fileHead, WiaDisc disc)
    {
        const long absoluteEntryCap = 4 * 1024 * 1024; // 4M entries = ~48 MiB group table
        var fileLength = stream.Length;
        var maxEntries = Math.Min((long)(fileHead.IsoFileSize / 0x8000) + 4096, absoluteEntryCap);

        if (disc.NumGroups > maxEntries)
        {
            throw new RvzFormatException(
                $"The group table declares {disc.NumGroups} entries for a "
                + $"{fileHead.IsoFileSize}-byte image.");
        }

        if (disc.NumRawDataEntries > maxEntries)
        {
            throw new RvzFormatException(
                $"The raw-data table declares {disc.NumRawDataEntries} entries for a "
                + $"{fileHead.IsoFileSize}-byte image.");
        }

        if (disc.NumPartitions > 4096)
        {
            throw new RvzFormatException(
                $"The partition table declares {disc.NumPartitions} entries.");
        }

        var partitionTableBytes = (long)disc.NumPartitions * disc.PartitionEntrySize;
        if (partitionTableBytes > fileLength ||
            (long)disc.PartitionEntriesOffset > fileLength - partitionTableBytes)
        {
            throw new RvzFormatException("The partition table lies outside the file.");
        }

        if (disc.RawDataEntriesSize > fileLength ||
            (long)disc.RawDataEntriesOffset > fileLength - disc.RawDataEntriesSize)
        {
            throw new RvzFormatException("The raw-data table lies outside the file.");
        }

        if (disc.GroupEntriesSize > fileLength ||
            (long)disc.GroupEntriesOffset > fileLength - disc.GroupEntriesSize)
        {
            throw new RvzFormatException("The group table lies outside the file.");
        }
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
    /// Thread-safe: concurrent calls share decoded units through the LRU cache and only
    /// serialize the short file reads.
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
    /// discs up to 2 GiB — use <see cref="ReadAt"/> or <see cref="IBlobReader.CopyTo(Stream, IProgress{double}, CancellationToken)"/> for larger
    /// images, e.g. Wii discs).
    /// </summary>
    public byte[] ReadFully()
    {
        return ReadFully(null, default);
    }

    /// <summary>
    /// Decodes the whole disc image into a single buffer, reporting progress and observing
    /// cancellation. The image must fit in memory (byte arrays are capped at
    /// <see cref="int.MaxValue"/> elements, so this supports discs up to 2 GiB — use
    /// <see cref="ReadAt"/> or <see cref="IBlobReader.CopyTo(Stream, IProgress{double}, CancellationToken)"/> for larger images, e.g. Wii discs).
    /// </summary>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes decoded.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>The decoded disc image bytes.</returns>
    /// <exception cref="RvzFormatException">
    /// The image is larger than 2 GiB (use <see cref="IBlobReader.CopyTo(Stream, IProgress{double}, CancellationToken)"/> for images that large), or
    /// decoding stopped before the end of the image.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public byte[] ReadFully(IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (Length > int.MaxValue)
        {
            throw new RvzFormatException(
                $"The image is {Length} bytes; ReadFully supports at most {int.MaxValue} bytes — "
                + "stream it with CopyTo instead.");
        }

        var output = new byte[Length];
        var position = 0L;
        while (position < Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Copy in bounded pieces so the span offsets stay within int range.
            var take = (int)Math.Min(1 << 20, Length - position);
            var read = ReadAt(position, output.AsSpan((int)position, take));
            if (read <= 0)
            {
                throw new RvzFormatException($"Read stopped at offset 0x{position:X}.");
            }

            position += read;
            progress?.Report((double)position / Length);
        }

        return output;
    }

    /// <summary>
    /// Streams the decoded disc image into <paramref name="destination"/> in bounded blocks
    /// (1 MiB per read), reporting progress and observing cancellation. Unlike
    /// <see cref="ReadFully()"/>, this supports images of any size, e.g. Wii discs.
    /// </summary>
    /// <param name="destination">The stream that receives the decoded image bytes.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes decoded.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between reads.</param>
    /// <returns>The number of bytes copied (the image length).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="RvzFormatException">Decoding stopped before the end of the image.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public long CopyTo(Stream destination, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return BlobCopy.CopyTo(this, destination, progress, cancellationToken);
    }

    /// <summary>
    /// Streams the decoded disc image, optionally decoding chunks on a worker pool. Decoding
    /// is split into independent units (raw chunks and 64-sector partition regions); workers
    /// only serialize the short file reads, so LZMA/LZMA2 decompression runs in parallel.
    /// Results are written in disc order, so the output is byte-identical to
    /// <see cref="CopyTo(Stream, IProgress{double}, CancellationToken)"/> for any thread count.
    /// </summary>
    /// <param name="destination">The stream that receives the decoded image bytes.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the bytes decoded.
    /// </param>
    /// <param name="maxThreads">
    /// Decoding threads: 0 uses the processor count, 1 forces sequential decoding.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between batches.</param>
    /// <returns>The number of bytes copied (the image length).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="RvzFormatException">Decoding stopped before the end of the image.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public long CopyTo(Stream destination, IProgress<double>? progress, int maxThreads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var threads = maxThreads > 0 ? maxThreads : Environment.ProcessorCount;
        if (threads <= 1)
        {
            return BlobCopy.CopyTo(this, destination, progress, cancellationToken);
        }

        return ParallelCopyTo(destination, progress, threads, cancellationToken);
    }

    private long ParallelCopyTo(Stream destination, IProgress<double>? progress, int threads,
        CancellationToken cancellationToken)
    {
        const long regionBytes = 64L * PartitionRegionBuilder.SectorSize;
        // Decoded payloads held per batch; keeps peak memory bounded for any chunk size.
        const long maxBatchBytes = 128L * 1024 * 1024;

        // The first 0x80 bytes of the image are served from the disc header (dhead).
        var copied = 0L;
        if (Length >= WiaDisc.DiscHeaderSize)
        {
            destination.Write(Disc.DiscHeader.AsSpan(0, WiaDisc.DiscHeaderSize));
            copied = WiaDisc.DiscHeaderSize;
            progress?.Report((double)copied / Length);
        }

        foreach (var area in _areas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (area.End <= copied)
            {
                continue; // already served by the disc header (an area can start at 0)
            }

            if (area.Start > copied)
            {
                throw new RvzFormatException(
                    $"No data covers disc offset 0x{copied:X}; the file is not a complete disc image.");
            }

            var areaSize = area.End - area.Start;
            var unitSize = area.Kind == AreaKind.Raw ? (long)Disc.ChunkSize : regionBytes;
            var unitCount = (areaSize + unitSize - 1) / unitSize;
            // The disc header can leave the current position in the middle of the first unit.
            var firstUnit = (copied - area.Start) / unitSize;
            var batchSize = (int)Math.Max(1, Math.Min((long)threads * 2, maxBatchBytes / unitSize));
            var results = new byte[batchSize][];

            for (long first = firstUnit; first < unitCount; first += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(batchSize, unitCount - first);
                if (count == 1)
                {
                    results[0] = DecodeUnit(area, first, unitSize);
                }
                else
                {
                    var First = first;
                    Parallel.For(0, count,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = threads,
                            CancellationToken = cancellationToken
                        },
                        i => results[i] = DecodeUnit(area, First + i, unitSize));
                }

                for (var i = 0; i < count; i++)
                {
                    var payload = results[i];
                    var unitStart = area.Start + (first + i) * unitSize;
                    var skip = (int)(copied - unitStart);
                    // The last unit of an area can be shorter than the unit size.
                    var take = (int)Math.Min(payload.Length - skip, area.End - copied);
                    if (take > 0)
                    {
                        destination.Write(payload, skip, take);
                        copied += take;
                    }

                    results[i] = null!;
                }

                progress?.Report((double)copied / Length);
            }
        }

        if (copied != Length)
        {
            throw new RvzFormatException(
                $"No data covers disc offset 0x{copied:X}; the file is not a complete disc image.");
        }

        return copied;
    }

    private byte[] DecodeUnit(DataArea area, long unitIndex, long unitSize)
    {
        if (area.Kind == AreaKind.Raw)
        {
            return DecodeRawChunk(area.Index, unitIndex, area.End - area.Start);
        }

        return DecodePartitionRegion(area, unitIndex);
    }

    private byte[] DecodeRawChunk(int rawIndex, long chunkIndex, long areaSize)
    {
        var entry = RawDataEntries[rawIndex];
        var groupIndex = entry.GroupIndex + chunkIndex;
        if (groupIndex >= GroupEntries.Length)
        {
            throw new RvzFormatException(
                $"Raw-data entry {rawIndex} references group {groupIndex}, but only "
                + $"{GroupEntries.Length} groups exist.");
        }

        var expectedSize = (int)Math.Min(Disc.ChunkSize, areaSize - chunkIndex * Disc.ChunkSize);
        return DecodeStoredGroup(GroupEntries[groupIndex], isPartition: false, expectedSize,
            chunkIndex * Disc.ChunkSize).Payload;
    }

    private byte[] DecodePartitionRegion(DataArea area, long regionIndex)
    {
        // One chunk payload can feed several sectors of a region; keep a per-region local map
        // so the parallel path decodes each chunk once without touching the shared LRU.
        var chunks = new Dictionary<long, (byte[] Payload, HashExceptionEntry[][] Lists)>();
        return BuildPartitionRegion(area, regionIndex, chunkIndex =>
        {
            if (!chunks.TryGetValue(chunkIndex, out var chunk))
            {
                chunk = ReadAndDecodePartitionChunk(area, chunkIndex);
                chunks[chunkIndex] = chunk;
            }

            return chunk;
        });
    }

    private (byte[] Payload, HashExceptionEntry[][] Lists) ReadAndDecodePartitionChunk(
        DataArea area, long chunkIndex)
    {
        var pd = Partitions[area.Index].Data[area.Segment];
        var sectorsPerChunk = Disc.ChunkSize / WiaDisc.SectorSize;
        var remainingSectors = pd.NumSectors - chunkIndex * sectorsPerChunk;
        var expectedSize =
            (int)(Math.Min(sectorsPerChunk, remainingSectors) * WiiHashCalculator.SectorDataSize);

        var groupIndex = pd.GroupIndex + chunkIndex;
        if (groupIndex >= GroupEntries.Length)
        {
            throw new RvzFormatException(
                $"Partition data entry references group {groupIndex}, but only "
                + $"{GroupEntries.Length} groups exist.");
        }

        var result = DecodeStoredGroup(GroupEntries[groupIndex], isPartition: true, expectedSize,
            chunkIndex * PartitionChunkPayloadSize);
        return (result.Payload, result.ExceptionLists);
    }

    /// <summary>
    /// Reads a group's stored bytes under the file lock and decodes them from memory, so
    /// worker threads only serialize the short reads (Dolphin decodes from per-thread buffers).
    /// </summary>
    private ChunkDecodeResult DecodeStoredGroup(GroupEntry group, bool isPartition,
        int expectedSize, long dataOffset)
    {
        if (group.StoredSize == 0)
        {
            // Special case: all zeroes, empty exception lists (ChunkDecoder.DecodeChunk).
            return new ChunkDecodeResult { Payload = new byte[expectedSize] };
        }

        // Untrusted size fields: never allocate or read beyond the actual file.
        if (group.StoredSize > int.MaxValue ||
            (long)group.FileOffset + group.StoredSize > _file.Length)
        {
            throw new RvzFormatException(
                $"Group at file offset 0x{group.FileOffset:X} with size {group.StoredSize} "
                + "lies outside the file.");
        }

        var storedSize = (int)group.StoredSize;
        var stored = ArrayPool<byte>.Shared.Rent(storedSize);
        try
        {
            lock (_fileLock)
            {
                if (!ReadExactlyAt(_file, (long)group.FileOffset, stored.AsSpan(0, storedSize)))
                {
                    throw new RvzFormatException(
                        $"Group at file offset 0x{group.FileOffset:X} is truncated.");
                }
            }

            using var memory = new MemoryStream(stored, 0, storedSize, writable: false);
            return ChunkDecoder.DecodeChunk(memory, Disc, _codec, new ChunkDecodeRequest
            {
                Group = new GroupEntry(0, group.StoredSize, group.UsesDiscCompression,
                    group.RvzPackedSize),
                IsPartition = isPartition,
                ExpectedSize = expectedSize,
                DataOffset = dataOffset
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stored);
        }
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
        return _cache.GetOrAdd(
            new CacheKey(KindRaw, rawIndex, 0, chunkIndex),
            _ => new CacheValue
            {
                Payload = DecodeRawChunk(rawIndex, chunkIndex, areaSize)
            },
            value => value.Payload.Length).Payload;
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
        return _cache.GetOrAdd(
            new CacheKey(KindPartitionRegion, area.Index, area.Segment, regionIndex),
            _ => new CacheValue { Payload = BuildPartitionRegion(area, regionIndex) },
            value => value.Payload.Length).Payload;
    }

    private byte[] BuildPartitionRegion(DataArea area, long regionIndex)
    {
        return BuildPartitionRegion(area, regionIndex,
            chunkIndex => GetPartitionChunk(area, chunkIndex));
    }

    private byte[] BuildPartitionRegion(DataArea area, long regionIndex,
        Func<long, (byte[] Payload, HashExceptionEntry[][] Lists)> getChunk)
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
            var (payload, lists) = getChunk(chunkIndex);
            var sectorData = payload.AsSpan(sectorInChunk * WiiHashCalculator.SectorDataSize,
                WiiHashCalculator.SectorDataSize);

            builder.AddSector(sectorData, GetSectorExceptions(chunkIndex, regionIndex, sector, lists));
        }

        return builder.Finish();
    }

    private (byte[] Payload, HashExceptionEntry[][] Lists) GetPartitionChunk(DataArea area, long chunkIndex)
    {
        var value = _cache.GetOrAdd(
            new CacheKey(KindPartitionChunk, area.Index, area.Segment, chunkIndex),
            _ =>
            {
                var (payload, lists) = ReadAndDecodePartitionChunk(area, chunkIndex);
                return new CacheValue { Payload = payload, Lists = lists };
            },
            cached => cached.Payload.Length);
        return (value.Payload, value.Lists);
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
        // _areas is sorted by Start and validated to be non-overlapping; find the last area
        // whose Start is <= offset (binary search) and require the offset to fall inside it.
        var low = 0;
        var high = _areas.Length - 1;
        DataArea? candidate = null;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (_areas[mid].Start <= offset)
            {
                candidate = _areas[mid];
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return candidate is { } area && offset < area.End ? area : null;
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
