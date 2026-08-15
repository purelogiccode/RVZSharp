using RVZSharp.IO;

namespace RVZSharp.Models;

/// <summary>
/// <c>wia_raw_data_t</c>: disc data that is not part of a Wii partition, stored as is
/// (other than compression). Offsets are disc-relative.
/// </summary>
public readonly struct WiaRawDataEntry
{
    public const int Size = 0x18;

    /// <summary>The offset on the disc at which this data starts.</summary>
    public ulong RawDataOffset { get; }

    /// <summary>The number of bytes on the disc covered by this entry.</summary>
    public ulong RawDataSize { get; }

    /// <summary>Index of the first group entry; the rest follow sequentially.</summary>
    public uint GroupIndex { get; }

    /// <summary>The number of group entries used for this data.</summary>
    public uint NumGroups { get; }

    public WiaRawDataEntry(ulong rawDataOffset, ulong rawDataSize, uint groupIndex, uint numGroups)
    {
        RawDataOffset = rawDataOffset;
        RawDataSize = rawDataSize;
        GroupIndex = groupIndex;
        NumGroups = numGroups;
    }

    public static WiaRawDataEntry Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        return new WiaRawDataEntry(
            reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32());
    }
}
