using System.Security.Cryptography;
using RVZSharp.Models;

namespace RVZSharp.Tests.Helpers;

/// <summary>Builds a structurally valid WiaDisc struct (0xDC bytes) for tests.</summary>
public sealed class TestDiscBuilder
{
    /// <summary>Disc type written to the disc struct (GameCube by default).</summary>
    public DiscType DiscType { get; set; } = DiscType.GameCube;

    /// <summary>Compression method written to the disc struct.</summary>
    public CompressionType Compression { get; set; } = CompressionType.None;

    /// <summary>Compression level written to the disc struct.</summary>
    public int ComprLevel { get; set; } = 3;

    /// <summary>Chunk size written to the disc struct (2 MiB by default).</summary>
    public uint ChunkSize { get; set; } = WiaDisc.GroupSize; // 2 MiB

    /// <summary>Disc header bytes written to the disc struct.</summary>
    public byte[] DiscHeader { get; set; } = new byte[WiaDisc.DiscHeaderSize];

    /// <summary>Number of partition entries.</summary>
    public uint NumPartitions { get; set; }

    /// <summary>Size of each partition entry (0x30).</summary>
    public uint PartitionEntrySize { get; set; } = 0x30;

    /// <summary>Partition table offset.</summary>
    public ulong PartitionEntriesOffset { get; set; }

    /// <summary>SHA-1 of the partition table.</summary>
    public byte[] PartitionEntriesHash { get; set; } = new byte[WiaDisc.HashSize];

    /// <summary>Number of raw data entries.</summary>
    public uint NumRawDataEntries { get; set; }

    /// <summary>Raw data table offset.</summary>
    public ulong RawDataEntriesOffset { get; set; }

    /// <summary>Raw data table size.</summary>
    public uint RawDataEntriesSize { get; set; }

    /// <summary>Number of group entries.</summary>
    public uint NumGroups { get; set; }

    /// <summary>Group table offset.</summary>
    public ulong GroupEntriesOffset { get; set; }

    /// <summary>Group table size.</summary>
    public uint GroupEntriesSize { get; set; }

    /// <summary>Length of the compression data blob (codec properties).</summary>
    public byte ComprDataLen { get; set; }

    /// <summary>Compression data blob (codec properties).</summary>
    public byte[] ComprData { get; set; } = new byte[WiaDisc.ComprDataCapacity];

    /// <summary>Builds the 0xDC-byte disc struct from the current properties.</summary>
    public byte[] Build()
    {
        var b = new byte[WiaDisc.Size];
        WriteBe(b, 0, (uint)DiscType);
        WriteBe(b, 4, (uint)Compression);
        WriteBe(b, 8, (uint)ComprLevel);
        WriteBe(b, 12, ChunkSize);
        DiscHeader.CopyTo(b, 16);
        WriteBe(b, 16 + 0x80, NumPartitions);
        WriteBe(b, 20 + 0x80, PartitionEntrySize);
        WriteBe(b, 24 + 0x80, PartitionEntriesOffset);
        PartitionEntriesHash.CopyTo(b, 32 + 0x80);
        WriteBe(b, 52 + 0x80, NumRawDataEntries);
        WriteBe(b, 56 + 0x80, RawDataEntriesOffset);
        WriteBe(b, 64 + 0x80, RawDataEntriesSize);
        WriteBe(b, 68 + 0x80, NumGroups);
        WriteBe(b, 72 + 0x80, GroupEntriesOffset);
        WriteBe(b, 80 + 0x80, GroupEntriesSize);
        b[84 + 0x80] = ComprDataLen;
        ComprData.CopyTo(b, 85 + 0x80);
        return b;
    }

    /// <summary>Writes the disc hash into a file head builder (hash over the built disc bytes).</summary>
    public byte[] DiscHash
    {
        set => _discHashOverride = value;
    }

    private byte[]? _discHashOverride;

    /// <summary>
    /// Returns the disc hash: the override set through <see cref="DiscHash"/> when present,
    /// otherwise the SHA-1 of the built disc struct.
    /// </summary>
    public byte[] GetDiscHash()
    {
        return _discHashOverride ?? SHA1.HashData(Build());
    }

    private static void WriteBe(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    private static void WriteBe(byte[] b, int offset, ulong value)
    {
        WriteBe(b, offset, (uint)(value >> 32));
        WriteBe(b, offset + 4, (uint)value);
    }
}
