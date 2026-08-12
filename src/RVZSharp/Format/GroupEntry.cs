namespace RVZSharp.Format;

/// <summary>
/// One group chunk in the common form used by the decoder, expanded from either an
/// <see cref="RvzGroupEntry"/> (RVZ) or a <see cref="WiaGroupEntry"/> (WIA).
/// </summary>
public readonly struct GroupEntry
{
    /// <summary>Offset of the group data in the file.</summary>
    public ulong FileOffset { get; }

    /// <summary>Size of the stored data (compressed or not), including exception lists and
    /// any NONE/PURGE padding. 0 means the group is all zeroes with empty exception lists.</summary>
    public uint StoredSize { get; }

    /// <summary>
    /// True if the data is stored with the disc's compression method. RVZ groups without the
    /// high bit are stored with method NONE; WIA groups always use the disc method.
    /// </summary>
    public bool UsesDiscCompression { get; }

    /// <summary>
    /// Size after decompressing but before decoding the RVZ packing; 0 means no packing.
    /// Always 0 for WIA.
    /// </summary>
    public uint RvzPackedSize { get; }

    public GroupEntry(ulong fileOffset, uint storedSize, bool usesDiscCompression, uint rvzPackedSize)
    {
        FileOffset = fileOffset;
        StoredSize = storedSize;
        UsesDiscCompression = usesDiscCompression;
        RvzPackedSize = rvzPackedSize;
    }

    public static GroupEntry FromRvz(RvzGroupEntry entry) =>
        new(entry.FileOffset, entry.StoredSize, entry.UsesDiscCompression, entry.RvzPackedSize);

    public static GroupEntry FromWia(WiaGroupEntry entry) =>
        new(entry.FileOffset, entry.StoredSize, usesDiscCompression: true, rvzPackedSize: 0);
}
