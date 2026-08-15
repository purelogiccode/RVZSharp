using System.Security.Cryptography;
using RVZSharp.Models;

namespace RVZSharp.Tests.Helpers;

/// <summary>Builds a structurally valid WiaDisc struct (0xDC bytes) for tests.</summary>
public sealed class TestDiscBuilder
{
    public DiscType DiscType { get; set; } = DiscType.GameCube;
    public CompressionType Compression { get; set; } = CompressionType.None;
    public int ComprLevel { get; set; } = 3;
    public uint ChunkSize { get; set; } = WiaDisc.GroupSize; // 2 MiB
    public byte[] DiscHeader { get; set; } = new byte[WiaDisc.DiscHeaderSize];
    public uint NumPartitions { get; set; }
    public uint PartitionEntrySize { get; set; } = 0x30;
    public ulong PartitionEntriesOffset { get; set; }
    public byte[] PartitionEntriesHash { get; set; } = new byte[WiaDisc.HashSize];
    public uint NumRawDataEntries { get; set; }
    public ulong RawDataEntriesOffset { get; set; }
    public uint RawDataEntriesSize { get; set; }
    public uint NumGroups { get; set; }
    public ulong GroupEntriesOffset { get; set; }
    public uint GroupEntriesSize { get; set; }
    public byte ComprDataLen { get; set; }
    public byte[] ComprData { get; set; } = new byte[WiaDisc.ComprDataCapacity];

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

    public byte[] GetDiscHash() => _discHashOverride ?? SHA1.HashData(Build());

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
