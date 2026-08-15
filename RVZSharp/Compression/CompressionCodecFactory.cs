using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Compression;

/// <summary>Creates <see cref="ICompressionDecoder"/> instances for RVZ compression methods.</summary>
public static class CompressionCodecFactory
{
    public static ICompressionDecoder Create(CompressionType type) => type switch
    {
        CompressionType.None => NoneDecoder.Instance,
        CompressionType.Bzip2 => new Bzip2Decoder(),
        CompressionType.Lzma => new LzmaDecoder(useLzma2: false),
        CompressionType.Lzma2 => new LzmaDecoder(useLzma2: true),
        CompressionType.Zstd => new ZstdDecoder(),
        CompressionType.Purge => throw new RvzUnsupportedException(
            "The PURGE compression method is WIA-only and not supported in RVZ."),
        _ => throw new RvzUnsupportedException(
            $"Unsupported compression method {(uint)type}."),
    };
}
