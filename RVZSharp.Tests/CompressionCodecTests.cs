using ICSharpCode.SharpZipLib.BZip2;
using RVZSharp;
using RVZSharp.Compression;
using RVZSharp.Models;

namespace RVZSharp.Tests;

public class CompressionCodecTests
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

    [Fact]
    public void None_Passthrough()
    {
        var payload = MakePayload(100_000);
        using var input = new MemoryStream(payload);
        var decoder = CompressionCodecFactory.Create(CompressionType.None);
        using var stream = decoder.CreateDecompressor(input, [], payload.Length, payload.Length);

        Assert.Equal(payload, DecompressAll(stream, payload.Length));
    }

    [Fact]
    public void None_WithProperties_Throws()
    {
        var decoder = CompressionCodecFactory.Create(CompressionType.None);
        Assert.Throws<RvzFormatException>(() =>
            decoder.CreateDecompressor(new MemoryStream(), [1], -1, -1));
    }

    [Fact]
    public void Zstd_RoundTrip()
    {
        var payload = MakePayload(200_000);
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var cs = new ZstdSharp.CompressionStream(ms, 3, 0, leaveOpen: true))
            {
                cs.Write(payload);
            }

            compressed = ms.ToArray();
        }

        using var input = new MemoryStream(compressed);
        var decoder = CompressionCodecFactory.Create(CompressionType.Zstd);
        using var stream = decoder.CreateDecompressor(input, [], compressed.Length, payload.Length);

        Assert.Equal(payload, DecompressAll(stream, payload.Length));
    }

    [Fact]
    public void Zstd_NegativeFastLevel_RoundTrip()
    {
        // Dolphin's CLI accepts ZSTD_minCLevel()..ZSTD_maxCLevel() (WIABlob.cpp:68-75);
        // negative levels select fast modes and must not be clamped to 1.
        var payload = MakePayload(200_000);
        var (encoder, props) = CompressionEncoderFactory.Create(CompressionType.Zstd, -5);
        var compressed = encoder.Compress(payload);

        using var input = new MemoryStream(compressed);
        var decoder = CompressionCodecFactory.Create(CompressionType.Zstd);
        using var stream = decoder.CreateDecompressor(input, props, compressed.Length, payload.Length);

        Assert.Equal(payload, DecompressAll(stream, payload.Length));
    }

    [Fact]
    public void Zstd_WithProperties_Throws()
    {
        var decoder = CompressionCodecFactory.Create(CompressionType.Zstd);
        Assert.Throws<RvzFormatException>(() =>
            decoder.CreateDecompressor(new MemoryStream(), [1], -1, -1));
    }

    [Fact]
    public void Bzip2_RoundTrip()
    {
        var payload = MakePayload(200_000);
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var cs = new BZip2OutputStream(ms) { IsStreamOwner = false })
            {
                cs.Write(payload, 0, payload.Length);
            }

            compressed = ms.ToArray();
        }

        using var input = new MemoryStream(compressed);
        var decoder = CompressionCodecFactory.Create(CompressionType.Bzip2);
        using var stream = decoder.CreateDecompressor(input, [], compressed.Length, payload.Length);

        Assert.Equal(payload, DecompressAll(stream, payload.Length));
    }

    [Fact]
    public void Bzip2_WithProperties_Throws()
    {
        var decoder = CompressionCodecFactory.Create(CompressionType.Bzip2);
        Assert.Throws<RvzFormatException>(() =>
            decoder.CreateDecompressor(new MemoryStream(), [1], -1, -1));
    }
}
