using RVZSharp.Packing;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class PackingTests
{
    private static byte[] MakeSeed(int seedValue)
    {
        var seed = new byte[68];
        var rng = new Random(seedValue);
        rng.NextBytes(seed);
        return seed;
    }

    private static byte[] GenerateJunk(byte[] seed, long offset, int count)
    {
        return ReferencePrng.Generate(seed, offset, count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(42)]
    [InlineData(0x1234)]
    [InlineData(0x7FFF)]
    [InlineData(0x8000)] // boundary: no skip
    [InlineData(0x12345)]
    public void Prng_MatchesDolphinReference_AcrossOffsets(int seedValue)
    {
        var seed = MakeSeed(seedValue);
        var offset = seedValue * 0x123;
        const int count = 100_000;

        var expected = GenerateJunk(seed, offset, count);
        var actual = new byte[count];

        var prng = new LaggedFibonacciPrng();
        prng.SetSeed(seed);
        prng.Forward(offset % 0x8000);
        prng.GetBytes(actual, count);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Prng_OutputIsDeterministic()
    {
        var seed = MakeSeed(7);
        var a = new byte[521 * 4 * 2];
        var b = new byte[521 * 4 * 2];
        var p1 = new LaggedFibonacciPrng();
        var p2 = new LaggedFibonacciPrng();
        p1.SetSeed(seed);
        p2.SetSeed(seed);
        p1.GetBytes(a, a.Length);
        p2.GetBytes(b, b.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PackingDecoder_LiteralOnly()
    {
        var payload = new byte[5000];
        new Random(3).NextBytes(payload);
        var packed = Concat(Be32((uint)payload.Length), payload);

        using var input = new MemoryStream(packed);
        using var decoder = new RvzPackingDecoder(input, dataOffset: 0);
        Assert.Equal(payload, ReadAll(decoder, payload.Length));
    }

    [Fact]
    public void PackingDecoder_PaddedOnly()
    {
        var seed = MakeSeed(11);
        var junk = GenerateJunk(seed, 0, 4096);
        var packed = Concat(Be32(0x8000_0000u | 4096), seed);

        using var input = new MemoryStream(packed);
        using var decoder = new RvzPackingDecoder(input, dataOffset: 0);
        Assert.Equal(junk, ReadAll(decoder, junk.Length));
    }

    [Fact]
    public void PackingDecoder_MixedSegments()
    {
        var seed1 = MakeSeed(1);
        var seed2 = MakeSeed(2);
        var literal = new byte[3000];
        new Random(9).NextBytes(literal);
        var junk1 = GenerateJunk(seed1, 0x20000 + literal.Length, 1000);
        var junk2 = GenerateJunk(seed2, 0x20000 + literal.Length + 1000, 2000);

        var packed = Concat(
            Be32((uint)literal.Length), literal,
            Be32(0x8000_0000u | 1000), seed1,
            Be32(0x8000_0000u | 2000), seed2);

        using var input = new MemoryStream(packed);
        using var decoder = new RvzPackingDecoder(input, dataOffset: 0x20000);
        var actual = ReadAll(decoder, literal.Length + junk1.Length + junk2.Length);

        Assert.Equal(Concat(literal, junk1, junk2), actual);
    }

    [Fact]
    public void PackingDecoder_SkipDependsOnDataOffset()
    {
        var seed = MakeSeed(5);
        var junkAt0 = GenerateJunk(seed, 0, 1000);
        var junkAtSkip = GenerateJunk(seed, 0x1234, 1000);

        var packed = Concat(Be32(0x8000_0000u | 1000), seed);

        using var input0 = new MemoryStream(packed);
        using var d0 = new RvzPackingDecoder(input0, dataOffset: 0);
        using var inputS = new MemoryStream(packed);
        using var dS = new RvzPackingDecoder(inputS, dataOffset: 0x1234);

        Assert.Equal(junkAt0, ReadAll(d0, 1000));
        Assert.Equal(junkAtSkip, ReadAll(dS, 1000));
    }

    [Fact]
    public void PackingDecoder_LiteralSegmentTruncated_Throws()
    {
        var packed = Concat(Be32(1000), new byte[100]);
        using var input = new MemoryStream(packed);
        using var decoder = new RvzPackingDecoder(input, 0);
        Assert.Throws<RvzFormatException>(() => ReadAll(decoder, 1000));
    }

    [Fact]
    public void PackingDecoder_SeedTruncated_Throws()
    {
        var packed = Concat(Be32(0x8000_0000u | 1000), new byte[30]);
        using var input = new MemoryStream(packed);
        using var decoder = new RvzPackingDecoder(input, 0);
        Assert.Throws<RvzFormatException>(() => ReadAll(decoder, 1000));
    }

    private static byte[] ReadAll(Stream stream, int expected)
    {
        var output = new byte[expected];
        var total = 0;
        while (total < expected)
        {
            var read = stream.Read(output, total, expected - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        Assert.Equal(expected, total);
        return output;
    }

    private static byte[] Be32(uint value)
    {
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
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
