namespace RVZSharp.Models;

/// <summary>One Wii disc partition found via the disc's partition table.</summary>
public readonly struct Partition
{
    public required ulong Offset { get; init; }
    public required uint Type { get; init; }

    /// <summary>data_offset (shifted) from the partition header: bytes from the partition start.</summary>
    public required ulong DataOffset { get; init; }

    /// <summary>data_size (shifted) from the partition header, in bytes.</summary>
    public required ulong DataSize { get; init; }

    /// <summary>The 16-byte title key from the partition's ticket.</summary>
    public required byte[] Key { get; init; }
}