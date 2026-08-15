using RVZSharp.Interfaces;
using RVZSharp.Models;

namespace RVZSharp.Compression;

/// <summary>
/// Creates <see cref="ICompressionEncoder"/> instances and the compr_data properties for
/// the disc header (Dolphin: SetUpCompressor + SetCompressorData).
/// </summary>
public static class CompressionEncoderFactory
{
    /// <summary>Creates an encoder; for PURGE also returns the props (always empty).</summary>
    public static (ICompressionEncoder Encoder, byte[] Properties) Create(
        CompressionType type, int level = 3)
    {
        switch (type)
        {
            case CompressionType.None:
                return (NoneEncoder.Instance, []);
            case CompressionType.Purge:
                return (new PurgeEncoder(), []);
            case CompressionType.Bzip2:
                return (new Bzip2Encoder(level), []);
            case CompressionType.Zstd:
                return (new ZstdEncoder(level), []);
            case CompressionType.Lzma:
            {
                var encoder = new LzmaEncoder(lzma2: false, level);
                return (encoder, encoder.Properties);
            }
            case CompressionType.Lzma2:
            {
                var encoder = new LzmaEncoder(lzma2: true, level);
                return (encoder, encoder.Properties);
            }
            default:
                throw new RvzUnsupportedException(
                    $"Unsupported compression method {(uint)type}.");
        }
    }
}
