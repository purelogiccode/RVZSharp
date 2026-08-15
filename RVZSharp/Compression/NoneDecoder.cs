using RVZSharp.Format;

namespace RVZSharp.Compression;

/// <summary>NONE compression: the "compressed" data is the data itself.</summary>
public sealed class NoneDecoder : ICompressionDecoder
{
    public static NoneDecoder Instance { get; } = new();

    public CompressionType Type => CompressionType.None;

    public Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize)
    {
        if (properties.Length != 0)
        {
            throw new RvzFormatException("NONE compression must not have compressor data.");
        }

        return input;
    }
}
