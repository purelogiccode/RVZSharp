using System.Runtime.InteropServices;
using RVZSharp.IO;

namespace RVZSharp.Models;

/// <summary>
/// <c>wia_raw_data_t</c>: disc data that is not part of a Wii partition, stored as is
/// (other than compression). Offsets are disc-relative.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct WiaRawDataEntry
{
    /// <summary>Size of the raw on-disk entry in bytes.</summary>
    public const int Size = 0x18;

    /// <summary>The offset on the disc at which this data starts.</summary>
    public ulong RawDataOffset { get; }

    /// <summary>The number of bytes on the disc covered by this entry.</summary>
    public ulong RawDataSize { get; }

    /// <summary>Index of the first group entry; the rest follow sequentially.</summary>
    public uint GroupIndex { get; }

    /// <summary>The number of group entries used for this data.</summary>
    public uint NumGroups { get; }

    /// <summary>Creates a raw data entry from its raw fields.</summary>
    /// <param name="rawDataOffset">The disc offset at which the data starts.</param>
    /// <param name="rawDataSize">The number of disc bytes covered by this entry.</param>
    /// <param name="groupIndex">Index of the first group entry.</param>
    /// <param name="numGroups">The number of group entries used for this data.</param>
    public WiaRawDataEntry(ulong rawDataOffset, ulong rawDataSize, uint groupIndex, uint numGroups)
    {
        RawDataOffset = rawDataOffset;
        RawDataSize = rawDataSize;
        GroupIndex = groupIndex;
        NumGroups = numGroups;
    }

    /// <summary>Parses one fixed 24-byte wia_raw_data_t entry from raw bytes.</summary>
    /// <param name="data">The raw bytes of the entry.</param>
    /// <returns>The parsed raw data entry.</returns>
    public static WiaRawDataEntry Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        return new WiaRawDataEntry(
            reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32());
    }
}
