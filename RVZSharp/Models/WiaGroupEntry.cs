using System.Runtime.InteropServices;
using RVZSharp.IO;

namespace RVZSharp.Models;

/// <summary>
/// The <c>wia_group_t</c> entry (0x08 bytes). Unlike RVZ groups, the most significant bit of
/// <c>data_size</c> carries no flag: the group is always stored with the disc's compression
/// method, and there is no packing stage.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct WiaGroupEntry
{
    public const int Size = 0x08;

    /// <summary>Offset in the file where the compressed data is stored, divided by 4.</summary>
    public uint DataOff4 { get; }

    /// <summary>Size of the stored data (0 = the group is all zeroes).</summary>
    public uint DataSize { get; }

    /// <summary>Size of the stored data; always used as-is (no compression flag bit).</summary>
    public uint StoredSize => DataSize;

    public WiaGroupEntry(uint dataOff4, uint dataSize)
    {
        DataOff4 = dataOff4;
        DataSize = dataSize;
    }

    /// <summary>Offset of the group data in the file.</summary>
    public ulong FileOffset => (ulong)DataOff4 << 2;

    public static WiaGroupEntry Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        return new WiaGroupEntry(reader.ReadUInt32(), reader.ReadUInt32());
    }
}
