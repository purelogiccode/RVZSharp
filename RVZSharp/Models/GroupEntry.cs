namespace RVZSharp.Models;

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

    /// <summary>Creates a group entry from its raw fields.</summary>
    /// <param name="fileOffset">Offset of the group data in the file.</param>
    /// <param name="storedSize">Size of the stored data, compressed or not.</param>
    /// <param name="usesDiscCompression">Whether the data uses the disc's compression method.</param>
    /// <param name="rvzPackedSize">Size after decompressing but before decoding the RVZ packing; 0 for none.</param>
    public GroupEntry(ulong fileOffset, uint storedSize, bool usesDiscCompression, uint rvzPackedSize)
    {
        FileOffset = fileOffset;
        StoredSize = storedSize;
        UsesDiscCompression = usesDiscCompression;
        RvzPackedSize = rvzPackedSize;
    }

    /// <summary>Expands an RVZ group entry into the common form.</summary>
    /// <param name="entry">The raw RVZ group entry to expand.</param>
    /// <returns>The equivalent common group entry.</returns>
    public static GroupEntry FromRvz(RvzGroupEntry entry)
    {
        return new GroupEntry(entry.FileOffset, entry.StoredSize, entry.UsesDiscCompression, entry.RvzPackedSize);
    }

    /// <summary>Expands a WIA group entry into the common form (always disc compression, no packing).</summary>
    /// <param name="entry">The raw WIA group entry to expand.</param>
    /// <returns>The equivalent common group entry.</returns>
    public static GroupEntry FromWia(WiaGroupEntry entry)
    {
        return new GroupEntry(entry.FileOffset, entry.StoredSize, usesDiscCompression: true, rvzPackedSize: 0);
    }
}
