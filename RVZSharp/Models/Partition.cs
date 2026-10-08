namespace RVZSharp.Models;

/// <summary>One Wii disc partition found via the disc's partition table.</summary>
public readonly struct Partition
{
    /// <summary>Partition offset on the disc, taken from the disc's partition table.</summary>
    public required ulong Offset { get; init; }

    /// <summary>Partition type value from the disc's partition table.</summary>
    public required uint Type { get; init; }

    /// <summary>data_offset (shifted) from the partition header: bytes from the partition start.</summary>
    public required ulong DataOffset { get; init; }

    /// <summary>data_size (shifted) from the partition header, in bytes.</summary>
    public required ulong DataSize { get; init; }

    /// <summary>
    /// The plaintext 16-byte partition key: the container's stored key for RVZ/WIA inputs, or
    /// the ticket title key (common-key decrypted) for plain ISO inputs.
    /// </summary>
    public required byte[] Key { get; init; }
}
