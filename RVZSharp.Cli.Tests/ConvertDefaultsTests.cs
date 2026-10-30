using RVZSharp.Models;

namespace RVZSharp.Cli.Tests;

/// <summary>
/// Tests for the convert command's defaults when <c>-b</c>, <c>-c</c> or <c>-l</c> are
/// omitted, so a plain <c>convert -i in -o out -f rvz</c> works without extra options.
/// </summary>
public class ConvertDefaultsTests
{
    /// <summary>Verifies the per-format block sizes used when -b is omitted.</summary>
    [Theory]
    [InlineData("gcz", 0x20000)]
    [InlineData("rvz", 0x20000)]
    [InlineData("wia", 0x200000)]
    [InlineData("ciso", 0x200000)]
    [InlineData("wbfs", 0x200000)]
    [InlineData("iso", 0)]
    [InlineData("tgc", 0)]
    public void DefaultBlockSize_MatchesFormat(string format, int expected)
    {
        Assert.Equal(expected, Program.DefaultBlockSize(format));
    }

    /// <summary>Verifies the per-format compression used when -c is omitted.</summary>
    [Theory]
    [InlineData("rvz", CompressionType.Zstd)]
    [InlineData("wia", CompressionType.Lzma2)]
    public void DefaultCompression_MatchesFormat(string format, CompressionType expected)
    {
        Assert.Equal(expected, Program.DefaultCompression(format));
    }
}
