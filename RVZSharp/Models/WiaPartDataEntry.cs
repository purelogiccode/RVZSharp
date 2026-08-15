using System.Runtime.InteropServices;
using RVZSharp.IO;

namespace RVZSharp.Models;

/// <summary>
/// <c>wia_part_data_t</c>: one segment of Wii partition data. Sectors are 32 KiB on the disc
/// (31 KiB of data excluding hashes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct WiaPartDataEntry
{
    /// <summary>Size of the raw on-disk entry in bytes.</summary>
    public const int Size = 0x10;

    /// <summary>The sector on the disc at which this data starts.</summary>
    public uint FirstSector { get; }

    /// <summary>The number of sectors covered by this entry.</summary>
    public uint NumSectors { get; }

    /// <summary>Index of the first group entry; the rest follow sequentially.</summary>
    public uint GroupIndex { get; }

    /// <summary>The number of group entries used for this data.</summary>
    public uint NumGroups { get; }

    /// <summary>Creates a partition data entry from its raw fields.</summary>
    /// <param name="firstSector">The disc sector at which the data starts.</param>
    /// <param name="numSectors">The number of sectors covered by this entry.</param>
    /// <param name="groupIndex">Index of the first group entry.</param>
    /// <param name="numGroups">The number of group entries used for this data.</param>
    public WiaPartDataEntry(uint firstSector, uint numSectors, uint groupIndex, uint numGroups)
    {
        FirstSector = firstSector;
        NumSectors = numSectors;
        GroupIndex = groupIndex;
        NumGroups = numGroups;
    }

    /// <summary>Parses one fixed 16-byte wia_part_data_t entry from raw bytes.</summary>
    /// <param name="data">The raw bytes of the entry.</param>
    /// <returns>The parsed partition data entry.</returns>
    public static WiaPartDataEntry Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        return new WiaPartDataEntry(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
    }
}
