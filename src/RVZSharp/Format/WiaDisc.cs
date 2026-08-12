using System.Security.Cryptography;
using RVZSharp.IO;

namespace RVZSharp.Format;

/// <summary>
/// The <c>wia_disc_t</c> struct, stored immediately after <see cref="WiaFileHead"/> (offset 0x48).
/// Dolphin's WIAHeader2 is 0xDC bytes; wit always writes the full 7 compr_data bytes.
/// </summary>
public readonly struct WiaDisc
{
    /// <summary>Full struct size including all 7 compr_data bytes (Dolphin: sizeof(WIAHeader2)).</summary>
    public const int Size = 0xDC;

    /// <summary>Minimum struct size, without compr_data (Dolphin: sizeof(WIAHeader2) - sizeof(compressor_data)).</summary>
    public const int MinSize = 0xD5;

    /// <summary>Size of the disc header bytes at the start of the disc image.</summary>
    public const int DiscHeaderSize = 0x80;

    /// <summary>Size of the SHA-1 hash fields.</summary>
    public const int HashSize = 20;

    /// <summary>Capacity of the compressor data array.</summary>
    public const int ComprDataCapacity = 7;

    /// <summary>32 KiB sector size of GameCube/Wii discs.</summary>
    public const uint SectorSize = 0x8000;

    /// <summary>2 MiB group size for Wii partition data.</summary>
    public const uint GroupSize = 0x200000;

    public DiscType DiscType { get; }
    public CompressionType Compression { get; }
    public int ComprLevel { get; }

    /// <summary>Chunk size data is divided into (RVZ: ≥ 32 KiB power of two, or multiple of 2 MiB).</summary>
    public uint ChunkSize { get; }

    /// <summary>The first 0x80 bytes of the disc image.</summary>
    public byte[] DiscHeader { get; }

    public uint NumPartitions { get; }
    public uint PartitionEntrySize { get; }
    public ulong PartitionEntriesOffset { get; }
    public byte[] PartitionEntriesHash { get; }

    public uint NumRawDataEntries { get; }
    public ulong RawDataEntriesOffset { get; }
    public uint RawDataEntriesSize { get; }

    public uint NumGroups { get; }
    public ulong GroupEntriesOffset { get; }
    public uint GroupEntriesSize { get; }

    /// <summary>Number of used bytes in <see cref="ComprData"/>.</summary>
    public byte ComprDataLen { get; }

    /// <summary>Compressor specific data (7-Zip LZMA props format for LZMA/LZMA2).</summary>
    public byte[] ComprData { get; }

    private WiaDisc(
        DiscType discType, CompressionType compression, int comprLevel, uint chunkSize,
        byte[] discHeader, uint numPartitions, uint partitionEntrySize, ulong partitionEntriesOffset,
        byte[] partitionEntriesHash, uint numRawDataEntries, ulong rawDataEntriesOffset,
        uint rawDataEntriesSize, uint numGroups, ulong groupEntriesOffset, uint groupEntriesSize,
        byte comprDataLen, byte[] comprData)
    {
        DiscType = discType;
        Compression = compression;
        ComprLevel = comprLevel;
        ChunkSize = chunkSize;
        DiscHeader = discHeader;
        NumPartitions = numPartitions;
        PartitionEntrySize = partitionEntrySize;
        PartitionEntriesOffset = partitionEntriesOffset;
        PartitionEntriesHash = partitionEntriesHash;
        NumRawDataEntries = numRawDataEntries;
        RawDataEntriesOffset = rawDataEntriesOffset;
        RawDataEntriesSize = rawDataEntriesSize;
        NumGroups = numGroups;
        GroupEntriesOffset = groupEntriesOffset;
        GroupEntriesSize = groupEntriesSize;
        ComprDataLen = comprDataLen;
        ComprData = comprData;
    }

    /// <summary>
    /// Decodes the disc struct. Requires at least <see cref="MinSize"/> bytes; compr_data
    /// bytes that are not present are zero-filled (Dolphin zero-fills missing fields the same way).
    /// </summary>
    public static WiaDisc Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinSize)
        {
            throw new RvzFormatException(
                $"Disc struct needs at least {MinSize} bytes, only {data.Length} available.");
        }

        var reader = new SpanReader(data);
        var discType = (DiscType)reader.ReadUInt32();
        var compression = (CompressionType)reader.ReadUInt32();
        var comprLevel = reader.ReadInt32();
        var chunkSize = reader.ReadUInt32();
        var discHeader = reader.ReadBytes(DiscHeaderSize).ToArray();
        var numPartitions = reader.ReadUInt32();
        var partitionEntrySize = reader.ReadUInt32();
        var partitionEntriesOffset = reader.ReadUInt64();
        var partitionEntriesHash = reader.ReadBytes(HashSize).ToArray();
        var numRawDataEntries = reader.ReadUInt32();
        var rawDataEntriesOffset = reader.ReadUInt64();
        var rawDataEntriesSize = reader.ReadUInt32();
        var numGroups = reader.ReadUInt32();
        var groupEntriesOffset = reader.ReadUInt64();
        var groupEntriesSize = reader.ReadUInt32();
        var comprDataLen = reader.ReadByte();
        var availableComprData = Math.Min(ComprDataCapacity, reader.Remaining);
        var comprData = new byte[ComprDataCapacity];
        reader.ReadBytes(availableComprData).CopyTo(comprData);

        return new WiaDisc(discType, compression, comprLevel, chunkSize, discHeader, numPartitions,
            partitionEntrySize, partitionEntriesOffset, partitionEntriesHash, numRawDataEntries,
            rawDataEntriesOffset, rawDataEntriesSize, numGroups, groupEntriesOffset, groupEntriesSize,
            comprDataLen, comprData);
    }

    /// <summary>
    /// Validates the disc struct: disc size, compressor data bounds, SHA-1 of the disc bytes,
    /// disc type, compression method and chunk size. Follows Dolphin's rules.
    /// </summary>
    /// <param name="discSize">The disc_size value from the file head.</param>
    /// <param name="rawDisc">The raw disc struct bytes, exactly <paramref name="discSize"/> long.</param>
    /// <param name="expectedDiscHash">The disc_hash value from the file head.</param>
    public void Validate(uint discSize, ReadOnlySpan<byte> rawDisc, ReadOnlySpan<byte> expectedDiscHash)
    {
        if (discSize < MinSize)
        {
            throw new RvzFormatException(
                $"Disc struct size {discSize} is smaller than the minimum {MinSize}.");
        }

        if (ComprDataLen > ComprDataCapacity || discSize < MinSize + ComprDataLen)
        {
            throw new RvzFormatException(
                $"Disc struct size {discSize} is too small for {ComprDataLen} bytes of compressor data.");
        }

        var actualHash = SHA1.HashData(rawDisc[..(int)discSize]);
        if (!actualHash.AsSpan().SequenceEqual(expectedDiscHash))
        {
            throw new RvzHashMismatchException("The disc struct SHA-1 does not match its contents.");
        }

        if (DiscType is not (DiscType.GameCube or DiscType.Wii))
        {
            throw new RvzFormatException($"Invalid disc type {(uint)DiscType} (expected 1 or 2).");
        }

        switch (Compression)
        {
            case CompressionType.None:
            case CompressionType.Bzip2:
            case CompressionType.Lzma:
            case CompressionType.Lzma2:
            case CompressionType.Zstd:
                break;
            case CompressionType.Purge:
                throw new RvzUnsupportedException("The PURGE compression method is WIA-only and not supported in RVZ.");
            default:
                throw new RvzUnsupportedException($"Unsupported compression method {(uint)Compression}.");
        }

        // RVZ: chunk size must be >= 32 KiB and a power of two, or a multiple of 2 MiB.
        var isPowerOfTwo = (ChunkSize & (ChunkSize - 1)) == 0;
        var validRvzChunkSize = (ChunkSize >= SectorSize && isPowerOfTwo) ||
                                ChunkSize % GroupSize == 0;
        if (!validRvzChunkSize)
        {
            throw new RvzFormatException($"Invalid chunk size {ChunkSize}.");
        }
    }
}
