using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Compression;

/// <summary>Zstandard (RFC 8878) streaming decompression via ZstdSharp.Port (pure managed).</summary>
public sealed class ZstdDecoder : ICompressionDecoder
{
    public CompressionType Type => CompressionType.Zstd;

    public Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize)
    {
        if (properties.Length != 0)
        {
            throw new RvzFormatException("Zstandard compression must not have compressor data.");
        }

        return new ZstdSharp.DecompressionStream(input, leaveOpen: true);
    }
}
