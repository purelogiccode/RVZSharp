namespace RVZSharp.Models;

/// <summary>
/// The CRC-32, MD5 and SHA-1 hashes of a decoded disc image, as computed by
/// <see cref="DiscHasher.Compute(Interfaces.IBlobReader, IProgress{double}, CancellationToken)"/>
/// (Dolphin: VolumeVerifier / DolphinTool verify).
/// </summary>
public sealed record DiscHashes
{
    /// <summary>IEEE CRC-32 (zlib) of the decoded image.</summary>
    public required uint Crc32 { get; init; }

    /// <summary>MD5 of the decoded image (16 bytes).</summary>
    public required byte[] Md5 { get; init; }

    /// <summary>SHA-1 of the decoded image (20 bytes).</summary>
    public required byte[] Sha1 { get; init; }

    /// <summary>Returns true when all three hashes equal <paramref name="other"/>'s.</summary>
    /// <param name="other">The hashes to compare with; null returns false.</param>
    public bool Matches(DiscHashes? other)
    {
        return other is not null &&
               Crc32 == other.Crc32 &&
               Md5.AsSpan().SequenceEqual(other.Md5) &&
               Sha1.AsSpan().SequenceEqual(other.Sha1);
    }
}
