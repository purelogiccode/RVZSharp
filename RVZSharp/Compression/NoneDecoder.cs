using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Compression;

/// <summary>NONE compression: the "compressed" data is the data itself.</summary>
public sealed class NoneDecoder : ICompressionDecoder
{
    /// <summary>The singleton NONE decoder instance.</summary>
    public static NoneDecoder Instance { get; } = new();

    /// <summary>Gets the compression method this decoder handles.</summary>
    public CompressionType Type => CompressionType.None;

    /// <summary>
    /// Returns <c>input</c> as the "decompressed" stream after checking that no
    /// compressor data is present.
    /// </summary>
    /// <param name="input">Stream of the stored (uncompressed) data.</param>
    /// <param name="properties">The compressor properties; must be empty.</param>
    /// <param name="inputSize">Ignored.</param>
    /// <param name="outputSize">Ignored.</param>
    /// <returns>The input stream itself.</returns>
    public Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize)
    {
        if (properties.Length != 0)
        {
            throw new RvzFormatException("NONE compression must not have compressor data.");
        }

        return input;
    }
}
