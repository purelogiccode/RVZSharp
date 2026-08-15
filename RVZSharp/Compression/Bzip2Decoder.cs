using ICSharpCode.SharpZipLib.BZip2;
using RVZSharp.Format;
using RVZSharp.IO;

namespace RVZSharp.Compression;

/// <summary>BZip2 streaming decompression via SharpZipLib (pure managed).</summary>
public sealed class Bzip2Decoder : ICompressionDecoder
{
    public CompressionType Type => CompressionType.Bzip2;

    public Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize)
    {
        if (properties.Length != 0)
        {
            throw new RvzFormatException("BZip2 compression must not have compressor data.");
        }

        // BZip2InputStream disposes its underlying stream; wrap to keep ownership with the caller.
        return new BZip2InputStream(new NonDisposingStream(input));
    }
}
