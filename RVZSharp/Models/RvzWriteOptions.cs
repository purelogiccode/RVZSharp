namespace RVZSharp.Models;

/// <summary>Options for <see cref="RvzWriter.Write"/>.</summary>
public sealed record RvzWriteOptions
{
    public static readonly RvzWriteOptions Default = new();

    /// <summary>Compression method (Dolphin's default: Zstandard).</summary>
    public CompressionType Compression { get; init; } = CompressionType.Zstd;

    /// <summary>Compression level (1-9; Zstandard allows up to 22).</summary>
    public int CompressionLevel { get; init; } = 3;

    /// <summary>Chunk size: a power of two between 32 KiB and 2 MiB (Dolphin's default: 2 MiB).</summary>
    public int ChunkSize { get; init; } = (int)WiaDisc.GroupSize;

    /// <summary>Whether to apply the RVZ packing (junk detection) stage.</summary>
    public bool Packing { get; init; } = true;
}