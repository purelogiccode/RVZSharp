using RVZSharp.IO;

namespace RVZSharp.Models;

/// <summary>
/// <c>wia_part_data_t</c>: one segment of Wii partition data. Sectors are 32 KiB on the disc
/// (31 KiB of data excluding hashes).
/// </summary>
public readonly struct WiaPartDataEntry
{
    public const int Size = 0x10;

    /// <summary>The sector on the disc at which this data starts.</summary>
    public uint FirstSector { get; }

    /// <summary>The number of sectors covered by this entry.</summary>
    public uint NumSectors { get; }

    /// <summary>Index of the first group entry; the rest follow sequentially.</summary>
    public uint GroupIndex { get; }

    /// <summary>The number of group entries used for this data.</summary>
    public uint NumGroups { get; }

    public WiaPartDataEntry(uint firstSector, uint numSectors, uint groupIndex, uint numGroups)
    {
        FirstSector = firstSector;
        NumSectors = numSectors;
        GroupIndex = groupIndex;
        NumGroups = numGroups;
    }

    public static WiaPartDataEntry Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        return new WiaPartDataEntry(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
    }
}
