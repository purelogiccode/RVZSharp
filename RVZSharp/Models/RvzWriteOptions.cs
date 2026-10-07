namespace RVZSharp.Models;

/// <summary>Options for <see cref="RvzWriter.Write"/> and <see cref="WiaWriter.Write"/>.</summary>
public sealed record RvzWriteOptions
{
    /// <summary>The default options: Zstandard, level 3, 2 MiB chunks, packing enabled.</summary>
    public static readonly RvzWriteOptions Default = new();

    /// <summary>
    /// The default options for <see cref="WiaWriter"/>: LZMA2, level 3, 2 MiB chunks
    /// (WIA does not support Zstandard or packing).
    /// </summary>
    public static readonly RvzWriteOptions WiaDefault = new() { Compression = CompressionType.Lzma2 };

    /// <summary>Compression method (Dolphin's default: Zstandard).</summary>
    public CompressionType Compression { get; init; } = CompressionType.Zstd;

    /// <summary>Compression level (1-9; Zstandard allows up to 22).</summary>
    public int CompressionLevel { get; init; } = 3;

    /// <summary>Chunk size: a power of two between 32 KiB and 2 MiB (Dolphin's default: 2 MiB).</summary>
    public int ChunkSize { get; init; } = (int)WiaDisc.GroupSize;

    /// <summary>Whether to apply the RVZ packing (junk detection) stage.</summary>
    public bool Packing { get; init; } = true;

    /// <summary>
    /// Maximum number of threads used to compress groups (packing included); 0 uses the
    /// processor count. The output is byte-identical regardless of this setting, because
    /// groups are written in disc order (Dolphin: MultithreadedCompressor).
    /// </summary>
    public int MaxThreads { get; init; }
}
