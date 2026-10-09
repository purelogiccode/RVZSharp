using ICSharpCode.SharpZipLib.BZip2;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.IO;

namespace RVZSharp.Compression;

/// <summary>BZip2 streaming decompression via SharpZipLib (pure managed).</summary>
public sealed class Bzip2Decoder : ICompressionDecoder
{
    /// <summary>Gets the compression method this decoder handles.</summary>
    public CompressionType Type => CompressionType.Bzip2;

    /// <summary>
    /// Creates a bzip2 decompressor over <c>input</c>. The input stream is wrapped so its
    /// ownership stays with the caller.
    /// </summary>
    /// <param name="input">Stream of the bzip2 data.</param>
    /// <param name="properties">The compressor properties; ignored (Dolphin only passes
    /// compressor data to the LZMA decoders).</param>
    /// <param name="inputSize">Ignored by bzip2 framing.</param>
    /// <param name="outputSize">Ignored by bzip2 framing.</param>
    /// <returns>A read-only decompressing stream.</returns>
    public Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize)
    {
        // BZip2InputStream disposes its underlying stream; wrap to keep ownership with the
        // caller, and guard construction (it parses the first block eagerly).
        return CodecErrorStream.Create(
            () => new BZip2InputStream(new NonDisposingStream(input)), "bzip2");
    }
}
