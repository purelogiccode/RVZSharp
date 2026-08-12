using RVZSharp;
using RVZSharp.Compression;
using RVZSharp.Format;

namespace RVZSharp.Tests;

public class CompressionLzmaTests
{
    private static byte[] MakePayload(int size, int seed = 1)
    {
        var data = new byte[size];
        var rng = new Random(seed);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = i % 64 == 0 ? (byte)rng.Next(256) : (byte)(i * 31 % 251);
        }

        return data;
    }

    private static byte[] DecompressAll(Stream decompressor, long expectedSize)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        int n;
        while ((n = decompressor.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (total + n > expectedSize)
            {
                n = (int)(expectedSize - total);
            }

            ms.Write(buffer, 0, n);
            total += n;
            if (total == expectedSize)
            {
                break;
            }
        }

        return ms.ToArray();
    }

    /// <summary>Encodes a raw LZMA1 stream (7-Zip properties + data) using the LZMA-SDK (test-only).</summary>
    private static (byte[] Props, byte[] Data) EncodeLzma1(byte[] payload, bool endMarker)
    {
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        encoder.SetCoderProperties(
            [SevenZip.CoderPropID.DictionarySize, SevenZip.CoderPropID.PosStateBits,
             SevenZip.CoderPropID.LitContextBits, SevenZip.CoderPropID.LitPosBits],
            [1 << 20, 2, 3, 0]);

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

    [Theory]
    [InlineData(true)]  // end-of-stream marker present (Dolphin style)
    [InlineData(false)] // size-terminated only
    public void Lzma1_RoundTrip(bool endMarker)
    {
        var payload = MakePayload(150_000, seed: 7);
        var (props, compressed) = EncodeLzma1(payload, endMarker);

        using var input = new MemoryStream(compressed);
        var decoder = CompressionCodecFactory.Create(CompressionType.Lzma);
        using var stream = decoder.CreateDecompressor(input, props, compressed.Length, payload.Length);

        Assert.Equal(payload, DecompressAll(stream, payload.Length));
    }

    [Theory]
    [InlineData(new byte[] { }, "requires 5 bytes")]
    [InlineData(new byte[] { 0xFF, 0, 0, 0, 0 }, "properties byte")]
    [InlineData(new byte[] { 0, 0, 0, 0, 0x40 }, "dictionary size")]
    public void Lzma1_BadProperties_Throws(byte[] props, string messagePart)
    {
        var decoder = CompressionCodecFactory.Create(CompressionType.Lzma);
        var ex = Assert.ThrowsAny<RvzException>(() =>
            decoder.CreateDecompressor(new MemoryStream(), props, -1, -1));
        Assert.Contains(messagePart, ex.Message);
    }

    [Fact]
    public void Lzma2_UncompressedChunk_RoundTrip()
    {
        var payload = MakePayload(50_000, seed: 3); // <= 65536: the 16-bit size field of uncompressed LZMA2 chunks
        var compressed = BuildLzma2Stream(payload, compressedChunk: false);

        using var input = new MemoryStream(compressed);
        var decoder = CompressionCodecFactory.Create(CompressionType.Lzma2);
        using var stream = decoder.CreateDecompressor(input, [21], compressed.Length, payload.Length);

        Assert.Equal(payload, DecompressAll(stream, payload.Length));
    }

    [Fact]
    public void Lzma2_CompressedChunk_RoundTrip()
    {
        var payload = MakePayload(150_000, seed: 11);
        var compressed = BuildLzma2Stream(payload, compressedChunk: true);

        using var input = new MemoryStream(compressed);
        var decoder = CompressionCodecFactory.Create(CompressionType.Lzma2);
        using var stream = decoder.CreateDecompressor(input, [21], compressed.Length, payload.Length);

        Assert.Equal(payload, DecompressAll(stream, payload.Length));
    }

    [Fact]
    public void Lzma2_BadProperties_Throws()
    {
        var decoder = CompressionCodecFactory.Create(CompressionType.Lzma2);
        Assert.Throws<RvzFormatException>(() =>
            decoder.CreateDecompressor(new MemoryStream(), [41], -1, -1)); // > 40
        Assert.Throws<RvzFormatException>(() =>
            decoder.CreateDecompressor(new MemoryStream(), [], -1, -1)); // wrong length
    }

    /// <summary>
    /// Builds a raw LZMA2 stream, then the 0x00 end control byte.
    /// Property 21 → dictionary size (2 | 1) &lt;&lt; (10 + 11) = 3 MiB.
    /// Compressed chunks carry a size-terminated raw LZMA1 stream (no EOS marker, per the
    /// LZMA2 format); uncompressed chunks use control 0x01 (dict reset) + 0x02.
    /// </summary>
    private static byte[] BuildLzma2Stream(byte[] payload, bool compressedChunk)
    {
        using var outStream = new MemoryStream();
        if (compressedChunk)
        {
            var (props, lzma1Data) = EncodeLzma1(payload, endMarker: false);
            // Control 0xE0+: LZMA chunk with dictionary reset and new properties.
            var control = (byte)(0xE0 | ((payload.Length - 1) >> 16));
            outStream.WriteByte(control);
            outStream.WriteByte((byte)((payload.Length - 1) >> 8));
            outStream.WriteByte((byte)((payload.Length - 1) & 0xFF));
            // Packed size covers only the LZMA data, not the properties byte (liblzma semantics).
            outStream.WriteByte((byte)((lzma1Data.Length - 1) >> 8));
            outStream.WriteByte((byte)((lzma1Data.Length - 1) & 0xFF));
            outStream.WriteByte(props[0]); // lc/lp/pb byte (dict size comes from the container prop)
            outStream.Write(lzma1Data);
        }
        else
        {
            // Control 0x01 = uncompressed chunk with dictionary reset; size follows.
            outStream.WriteByte(0x01);
            outStream.WriteByte((byte)((payload.Length - 1) >> 8));
            outStream.WriteByte((byte)((payload.Length - 1) & 0xFF));
            outStream.Write(payload);
        }

        outStream.WriteByte(0x00); // end of LZMA2 stream
        return outStream.ToArray();
    }
}
