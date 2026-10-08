namespace RVZSharp.Models;

/// <summary>Options for <see cref="CisoWriter.Write(Interfaces.IBlobReader, Stream, CisoWriteOptions, IProgress{double}, CancellationToken)"/>.</summary>
public sealed record CisoWriteOptions
{
    /// <summary>The default options: 2 MiB blocks (the block size used by wit for CISO/WBI).</summary>
    public static readonly CisoWriteOptions Default = new();

    /// <summary>
    /// Block size in bytes. The CISO block map has 0x7FF8 entries, so the decoded image is
    /// <c>BlockSize × 0x7FF8</c> bytes; the image must fit in that capacity. The default is
    /// 2 MiB, which covers a Wii double-layer disc.
    /// </summary>
    public int BlockSize { get; init; } = 0x200000;

    /// <summary>
    /// Whether to zero the data of non-game Wii partitions (update/channel) before encoding,
    /// like Dolphin's DiscScrubber. Scrubbed blocks are all zero and are therefore stored
    /// absent, which makes the file much smaller. Ignored for GameCube discs and Wii images
    /// without a game partition (ScrubbedBlob.Create returns null).
    /// </summary>
    public bool Scrub { get; init; }
}
