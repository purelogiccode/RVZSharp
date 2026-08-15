using RVZSharp.Models;

namespace RVZSharp.Interfaces;

/// <summary>
/// Creates streaming decompressors for one RVZ compression method. The RVZ container only needs
/// decompression; encoding support (for writing RVZ files) can be added later behind the same type.
/// </summary>
public interface ICompressionDecoder
{
    /// <summary>The compression method this decoder handles.</summary>
    CompressionType Type { get; }

    /// <summary>
    /// Creates a decompressor over <paramref name="input"/>, which must contain exactly the
    /// compressed bytes (bounded by the caller, e.g. via <see cref="IO.SectionStream"/>).
    /// The returned stream is read-only and must be disposed by the caller; it does not dispose
    /// <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Stream of compressed data.</param>
    /// <param name="properties">The compressor properties from the disc header (compr_data).</param>
    /// <param name="inputSize">Exact compressed size, or -1 if unknown.</param>
    /// <param name="outputSize">Expected decompressed size, or -1 if unknown (stream until end).</param>
    /// <returns>A read-only stream of decompressed data; does not dispose the input stream.</returns>
    Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize);
}
