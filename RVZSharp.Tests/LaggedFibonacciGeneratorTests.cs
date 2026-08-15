using RVZSharp.Packing;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// The Lagged-Fibonacci generator used for Wii padding: seed recovery (GetSeed) must find a
/// seed whose stream matches the junk at the requested position, and the pack encoder must
/// round-trip chunks with junk at aligned and unaligned offsets.
/// </summary>
public class LaggedFibonacciGeneratorTests
{
    public static TheoryData<long> Offsets =>
    [
        0, 1, 3, 4, 0x7C00, 0x8000, 0x8001, 0x1234, 0x12345, 0x20000, 0x1FFC0
    ];

    [Theory]
    [MemberData(nameof(Offsets))]
    public void GetSeed_RecoversSeed_ThatRegeneratesTheJunk(long offset)
    {
        var seed = new byte[68];
        new Random(3).NextBytes(seed);
        var junk = ReferencePrng.Generate(seed, offset, 0x2000);

        var (recovered, reconstructed) =
            LaggedFibonacciGenerator.GetSeed(junk, junk.Length, offset % 0x8000);
        Assert.True(recovered.Length > 0, "GetSeed failed on genuine junk");
        Assert.Equal(junk.Length, reconstructed);

        var generator = new LaggedFibonacciGenerator();
        generator.SetSeed(recovered);
        generator.ForwardBytes(offset % 0x8000);
        var regenerated = new byte[junk.Length];
        generator.GetBytes(regenerated.Length, regenerated);
        Assert.Equal(junk, regenerated);
    }

    [Fact]
    public void GetSeed_RejectsRandomData()
    {
        var data = new byte[0x3000];
        new Random(9).NextBytes(data);
        var (seed, reconstructed) = LaggedFibonacciGenerator.GetSeed(data, data.Length, 0);
        Assert.True(reconstructed < data.Length,
            "random data must not reconstruct: " + reconstructed);
        _ = seed;
    }

    [Fact]
    public void Generator_MatchesReferencePrng()
    {
        var seed = new byte[68];
        new Random(5).NextBytes(seed);
        var generator = new LaggedFibonacciGenerator();
        generator.SetSeed(seed);
        var output = new byte[0x3000];
        generator.GetBytes(output.Length, output);
        Assert.Equal(ReferencePrng.Generate(seed, 0, output.Length), output);
    }
}
