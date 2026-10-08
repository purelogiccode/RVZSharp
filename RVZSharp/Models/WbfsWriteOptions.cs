namespace RVZSharp.Models;

/// <summary>Options for <see cref="WbfsWriter.Write(Interfaces.IBlobReader, Stream, WbfsWriteOptions, IProgress{double}, CancellationToken)"/>.</summary>
public sealed record WbfsWriteOptions
{
    /// <summary>The default options: 2 MiB WBFS clusters (the cluster size used by wit).</summary>
    public static readonly WbfsWriteOptions Default = new();

    /// <summary>
    /// WBFS cluster size in bytes; must be a power of two of at least 32 KiB. The disc block
    /// map stores u16 volume cluster indices, so the disc must fit in 65535 clusters: with the
    /// default 2 MiB clusters a Wii double-layer disc uses ~4500 of them.
    /// </summary>
    public int BlockSize { get; init; } = 0x200000;

    /// <summary>
    /// Whether to zero the data of non-game Wii partitions (update/channel) before encoding,
    /// like Dolphin's DiscScrubber. Scrubbed clusters are all zero and share one zero-filled
    /// volume cluster, which makes the file much smaller. Ignored for Wii images without a
    /// game partition (ScrubbedBlob.Create returns null).
    /// </summary>
    public bool Scrub { get; init; }
}
