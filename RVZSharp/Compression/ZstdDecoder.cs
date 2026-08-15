using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Compression;

/// <summary>Zstandard (RFC 8878) streaming decompression via ZstdSharp.Port (pure managed).</summary>
public sealed class ZstdDecoder : ICompressionDecoder
{
    /// <summary>Gets the compression method this decoder handles.</summary>
    public CompressionType Type => CompressionType.Zstd;

    /// <summary>
    /// Creates a Zstandard decompressor over <c>input</c> after checking that no compressor
    /// data is present.
    /// </summary>
    /// <param name="input">Stream of the Zstandard data.</param>
    /// <param name="properties">The compressor properties; must be empty.</param>
    /// <param name="inputSize">Ignored.</param>
    /// <param name="outputSize">Ignored.</param>
    /// <returns>A read-only decompressing stream.</returns>
    public Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize)
    {
        if (properties.Length != 0)
        {
            throw new RvzFormatException("Zstandard compression must not have compressor data.");
        }

        return new ZstdSharp.DecompressionStream(input, leaveOpen: true);
    }
}
