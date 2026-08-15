using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.IO;

namespace RVZSharp.Compression;

/// <summary>
/// LZMA1 / LZMA2 streaming decompression. Uses the vendored 7-Zip SDK decoder
/// (see <see cref="Lzma.LzmaStream"/>, MIT, adapted from SharpCompress).
/// The property formats are exactly the 7-Zip ones stored in RVZ: LZMA1 = 5 bytes
/// (lc/lp/pb byte + 4-byte little-endian dictionary size), LZMA2 = 1 byte (dictionary size).
/// </summary>
public sealed class LzmaDecoder : ICompressionDecoder
{
    private readonly bool _useLzma2;

    internal LzmaDecoder(bool useLzma2) => _useLzma2 = useLzma2;

    public CompressionType Type => _useLzma2 ? CompressionType.Lzma2 : CompressionType.Lzma;

    public Stream CreateDecompressor(Stream input, ReadOnlySpan<byte> properties, long inputSize, long outputSize)
    {
        if (_useLzma2)
        {
            if (properties.Length != 1)
            {
                throw new RvzFormatException($"LZMA2 requires 1 byte of compressor data, got {properties.Length}.");
            }

            if (properties[0] > 40)
            {
                throw new RvzFormatException($"Invalid LZMA2 dictionary size property {properties[0]}.");
            }

            if (properties[0] == 40)
            {
                throw new RvzUnsupportedException(
                    "LZMA2 dictionary sizes of 4 GiB (property 40) are not supported.");
            }
        }
        else
        {
            if (properties.Length != 5)
            {
                throw new RvzFormatException($"LZMA requires 5 bytes of compressor data, got {properties.Length}.");
            }

            if (properties[0] >= 9 * 5 * 5)
            {
                throw new RvzFormatException($"Invalid LZMA properties byte {properties[0]}.");
            }

            // The dictionary size is a 32-bit little-endian unsigned integer in the props.
            var dictSize = BinaryPrimitivesUInt32(properties);
            if (dictSize >= 0x40000000) // 1 GiB safety cap; real files use <= 64 MiB
            {
                throw new RvzUnsupportedException(
                    $"LZMA dictionary size {dictSize} is not supported (1 GiB cap).");
            }
        }

        return Lzma.LzmaStream.Create(
            properties.ToArray(), input, inputSize, outputSize,
            presetDictionary: null, isLzma2: _useLzma2, leaveOpen: true);
    }

    private static uint BinaryPrimitivesUInt32(ReadOnlySpan<byte> properties) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(properties.Slice(1, 4));
}
