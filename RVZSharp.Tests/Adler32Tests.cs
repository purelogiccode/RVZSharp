using System.Text;
using RVZSharp.IO;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class Adler32Tests
{
    [Fact]
    public void EmptyInput_ReturnsOne()
    {
        Assert.Equal(1u, Adler32.Compute([]));
    }

    [Theory]
    [InlineData("Wikipedia", 0x11E60398u)]
    [InlineData("123456789", 0x091E01DEu)]
    public void KnownVectors_MatchZlib(string text, uint expected)
    {
        Assert.Equal(expected, Adler32.Compute(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void SingleByte_Zero()
    {
        // a = 1 + 0 = 1, b = 0 + 1 = 1 → 0x00010001
        Assert.Equal(0x00010001u, Adler32.Compute([0]));
    }

    [Fact]
    public void SingleByte_255()
    {
        // a = 1 + 255 = 256, b = 0 + 256 = 256 → 0x01000100
        Assert.Equal(0x01000100u, Adler32.Compute([255]));
    }

    [Fact]
    public void ModulusWrap_ResetsTheAccumulators()
    {
        // After 65521 zero bytes: a stays 1 (1 + 0), b counts 1..65520 then wraps to 0, so the
        // checksum equals the empty-input checksum — proving both accumulators wrap at 65521.
        Assert.Equal(1u, Adler32.Compute(new byte[65521]));
    }

    [Fact]
    public void OneByteBeforeTheWrap_IsNotWrapped()
    {
        // 65520 zero bytes: b = 65520, a = 1 → 0xFFF00001.
        Assert.Equal(0xFFF00001u, Adler32.Compute(new byte[65520]));
    }

    [Fact]
    public void LargeRandomData_MatchesReference()
    {
        var data = new byte[64 * 1024];
        new Random(1234).NextBytes(data);

        Assert.Equal(TestLegacyBuilders.Adler32ForTest(data), Adler32.Compute(data));
    }

    [Fact]
    public void LastByteAlone_ChangesChecksum()
    {
        var data = new byte[1000];
        new Random(5).NextBytes(data);
        data[^1] ^= 0xFF;

        Assert.NotEqual(Adler32.Compute(data), Adler32.Compute(data[..^1].ToArray()));
    }
}