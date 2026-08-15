using RVZSharp.IO;

namespace RVZSharp.Format;

/// <summary>
/// The expanded <c>rvz_group_t</c> (0x0C bytes). Compared to WIA groups, the most significant
/// bit of <c>data_size</c> selects the compression method and <c>rvz_packed_size</c> tracks
/// the RVZ packing stage.
/// </summary>
public readonly struct RvzGroupEntry
{
    public const int Size = 0x0C;

    /// <summary>Flag: the group is stored with the disc's compression method (else NONE).</summary>
    private const uint CompressedFlag = 0x80000000;

    private const uint SizeMask = CompressedFlag - 1;

    /// <summary>Offset in the file where the compressed data is stored, divided by 4.</summary>
    public uint DataOff4 { get; }

    /// <summary>Raw data_size field (including the compression flag bit).</summary>
    public uint DataSize { get; }

    /// <summary>
    /// Size after decompressing but before decoding the RVZ packing; 0 means no packing.
    /// </summary>
    public uint RvzPackedSize { get; }

    public RvzGroupEntry(uint dataOff4, uint dataSize, uint rvzPackedSize)
    {
        DataOff4 = dataOff4;
        DataSize = dataSize;
        RvzPackedSize = rvzPackedSize;
    }

    /// <summary>Offset of the group data in the file.</summary>
    public ulong FileOffset => (ulong)DataOff4 << 2;

    /// <summary>True if the data is stored with the disc's compression method (false = NONE).</summary>
    public bool UsesDiscCompression => (DataSize & CompressedFlag) != 0;

    /// <summary>
    /// Size of the stored data (compressed or not), including exception lists and any NONE
    /// padding. 0 means the group is all zeroes with empty exception lists.
    /// </summary>
    public uint StoredSize => DataSize & SizeMask;

    public static RvzGroupEntry Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        return new RvzGroupEntry(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
    }
}
