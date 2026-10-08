using RVZSharp.Interfaces;

namespace RVZSharp.Models;

/// <summary>Options for <see cref="GczWriter.Write(IBlobReader, Stream, GczWriteOptions, IProgress{double}, CancellationToken)"/>.</summary>
public sealed record GczWriteOptions
{
    /// <summary>The default options: 16 KiB blocks, processor-count threads.</summary>
    public static readonly GczWriteOptions Default = new();

    /// <summary>
    /// Block size in bytes; must be a power of two (Dolphin's only rule for GCZ). The default
    /// is 16 KiB, the classic GCZ block size; Dolphin's GUI defaults to 128 KiB.
    /// </summary>
    public int BlockSize { get; init; } = 0x4000;

    /// <summary>
    /// Maximum number of threads used to compress blocks; 0 uses the processor count. The
    /// output is byte-identical regardless of this setting, because blocks are written in
    /// disc order (Dolphin: MultithreadedCompressor).
    /// </summary>
    public int MaxThreads { get; init; }

    /// <summary>
    /// Whether to zero the data of non-game Wii partitions (update/channel) before encoding,
    /// like Dolphin's DiscScrubber and the CLI's <c>--scrub</c>. Ignored for GameCube discs
    /// and Wii images without a game partition (ScrubbedBlob.Create returns null).
    /// </summary>
    public bool Scrub { get; init; }
}
