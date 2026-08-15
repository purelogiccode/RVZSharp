using ICSharpCode.SharpZipLib.BZip2;
using RVZSharp.Models;

namespace RVZSharp.Tests.Helpers;

/// <summary>Compresses test payloads with the same codecs RVZ writers use.</summary>
public static class TestCompressor
{
    public static byte[] Compress(CompressionType compression, byte[] data)
    {
        switch (compression)
        {
            case CompressionType.None:
                return data;

            case CompressionType.Zstd:
            {
                using var ms = new MemoryStream();
                using (var cs = new ZstdSharp.CompressionStream(ms, 3, 0, leaveOpen: true))
                {
                    cs.Write(data);
                }

                return ms.ToArray();
            }

            case CompressionType.Bzip2:
            {
                using var ms = new MemoryStream();
                using (var cs = new BZip2OutputStream(ms) { IsStreamOwner = false })
                {
                    cs.Write(data, 0, data.Length);
                }

                return ms.ToArray();
            }

            case CompressionType.Lzma:
            {
                // The props live in the disc header; the table holds only the stream.
                var (_, encoded) = EncodeLzma1(data, endMarker: true);
                return encoded;
            }

            case CompressionType.Lzma2:
            {
                return BuildLzma2Stream(data);
            }

            case CompressionType.Purge:
                return CompressPurge(data);

            default:
                throw new ArgumentOutOfRangeException(nameof(compression), compression, null);
        }
    }

    /// <summary>
    /// The WIA PURGE codec (mirrors Dolphin's PurgeCompressor): non-zero runs are stored as
    /// (u32 BE offset, u32 BE length) descriptors followed by the run's bytes; runs of more
    /// than 8 zeroes are skipped (they decode to zero-fill). The SHA-1 trailer covers the
    /// segment stream plus the exception lists that precede it in the group
    /// (Dolphin: AddPrecedingDataOnlyForPurgeHashing).
    /// </summary>
    public static byte[] CompressPurge(byte[] data, byte[]? precedingData = null)
    {
        const int segmentSize = 8;
        using var segments = new MemoryStream();
        var bytesRead = 0;
        while (true)
        {
            var firstNonZero = bytesRead;
            while (firstNonZero < data.Length && data[firstNonZero] == 0)
            {
                firstNonZero++;
            }

            if (firstNonZero == data.Length)
            {
                break;
            }

            var nonZeroEnd = firstNonZero;
            var sequenceLength = 0;
            for (var i = firstNonZero; i < data.Length; i++)
            {
                if (data[i] == 0)
                {
                    if (++sequenceLength > segmentSize)
                    {
                        break;
                    }
                }
                else
                {
                    sequenceLength = 0;
                    nonZeroEnd = i + 1;
                }
            }

            var length = nonZeroEnd - firstNonZero;
            WriteBe32(segments, (uint)firstNonZero);
            WriteBe32(segments, (uint)length);
            segments.Write(data, firstNonZero, length);
            bytesRead = nonZeroEnd;
        }

        var segmentBytes = segments.ToArray();
        var output = new MemoryStream();
        output.Write(segmentBytes);
        var hash = System.Security.Cryptography.SHA1.HashData(
            precedingData == null ? segmentBytes : [.. precedingData, .. segmentBytes]);
        output.Write(hash);
        return output.ToArray();
    }

    private static void WriteBe32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    /// <summary>Raw LZMA1: 5-byte 7-Zip properties + data (with EOS marker, Dolphin style).</summary>
    public static (byte[] Props, byte[] Data) EncodeLzma1(byte[] payload, bool endMarker)
    {
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        // LZMA-SDK ignores the outSize argument; the marker is controlled by CoderPropID.EndMarker.
        encoder.SetCoderProperties(
            [
                SevenZip.CoderPropID.DictionarySize, SevenZip.CoderPropID.PosStateBits,
                SevenZip.CoderPropID.LitContextBits, SevenZip.CoderPropID.LitPosBits,
                SevenZip.CoderPropID.EndMarker
            ],
            [1 << 20, 2, 3, 0, endMarker]);

        byte[] props;
        using (var ps = new MemoryStream())
        {
            encoder.WriteCoderProperties(ps);
            props = ps.ToArray();
        }

        using var input = new MemoryStream(payload);
        using var output = new MemoryStream();
        encoder.Code(input, output, payload.Length, endMarker ? -1 : payload.Length, null);
        return (props, output.ToArray());
    }

    /// <summary>
    /// Raw LZMA2 stream (prop byte 21 → 3 MiB dict): compressed chunks with dict reset +
    /// new properties (0xE0+), each wrapping a size-terminated LZMA1 stream, split so no
    /// chunk exceeds the 20-bit unpack size field (2 MiB), then the 0x00 end control.
    /// </summary>
    public static byte[] BuildLzma2Stream(byte[] payload)
    {
        // The pack-size field of an LZMA2 chunk is 16 bits (max 64 KiB of compressed data),
        // and each chunk is an independent LZMA stream with its own 5-byte range-coder init.
        // Random data compresses to roughly its own size, so keep parts well below 64 KiB.
        const int maxChunk = 0xF800;
        using var outStream = new MemoryStream();
        for (var offset = 0; offset < payload.Length; offset += maxChunk)
        {
            var part = payload.AsSpan(offset, Math.Min(maxChunk, payload.Length - offset)).ToArray();
            var (props, lzma1Data) = EncodeLzma1(part, endMarker: false);
            var control = (byte)(0xE0 | ((part.Length - 1) >> 16));
            var packedSize = lzma1Data.Length; // excludes the props byte (liblzma semantics)
            var header = new[]
            {
                control,
                (byte)((part.Length - 1) >> 8),
                (byte)((part.Length - 1) & 0xFF),
                (byte)((packedSize - 1) >> 8),
                (byte)((packedSize - 1) & 0xFF),
                props[0]
            };
            outStream.Write(header);
            outStream.Write(lzma1Data);
        }

        outStream.WriteByte(0x00);
        return outStream.ToArray();
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        var offset = 0;
        foreach (var a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }

        return result;
    }
}
