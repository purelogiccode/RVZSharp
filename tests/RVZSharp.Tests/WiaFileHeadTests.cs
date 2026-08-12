using RVZSharp;
using RVZSharp.Format;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class WiaFileHeadTests
{
    [Fact]
    public void Parse_ValidHeader_ReturnsAllFields()
    {
        var builder = new TestHeaderBuilder
        {
            Version = 0x01000000,
            VersionCompatible = 0x00030000,
            DiscSize = 0xDC,
            IsoFileSize = 0x100000000,
            RvzFileSize = 12345,
        };
        var bytes = builder.Build();

        var head = WiaFileHead.Parse(bytes);

        Assert.True(head.IsRvz);
        Assert.False(head.IsWia);
        Assert.Equal(0x01000000u, head.Version);
        Assert.Equal(0x00030000u, head.VersionCompatible);
        Assert.Equal(0xDCu, head.DiscSize);
        Assert.Equal(0x100000000ul, head.IsoFileSize);
        Assert.Equal(12345ul, head.RvzFileSize);
    }

    [Fact]
    public void Parse_Truncated_ThrowsFormatException()
    {
        var bytes = new TestHeaderBuilder().Build();
        Assert.Throws<RvzFormatException>(() => WiaFileHead.Parse(bytes.AsSpan(0, 0x30)));
    }

    [Fact]
    public void Validate_BadMagic_ThrowsFormatException()
    {
        var builder = new TestHeaderBuilder { Magic = "ABCD"u8.ToArray() };
        var bytes = builder.Build();
        var head = WiaFileHead.Parse(bytes);

        var ex = Assert.Throws<RvzFormatException>(() => head.Validate(bytes, bytes.Length));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WiaMagicWithRvzRules_ThrowsFormatException()
    {
        var builder = new TestHeaderBuilder { Magic = WiaFileHead.WiaMagic.ToArray() };
        var bytes = builder.Build();
        var head = WiaFileHead.Parse(bytes);

        // The RVZ overload rejects WIA magic; the WIA overload accepts it.
        Assert.Throws<RvzFormatException>(() => head.Validate(bytes, bytes.Length));
        head.Validate(bytes, bytes.Length, WiaRvzFormat.Wia); // must not throw
    }

    [Fact]
    public void Validate_VersionTooOld_ThrowsUnsupportedException()
    {
        var builder = new TestHeaderBuilder { Version = 0x00020000 };
        var bytes = builder.Build();
        var head = WiaFileHead.Parse(bytes);

        Assert.Throws<RvzUnsupportedException>(() => head.Validate(bytes, bytes.Length));
    }

    [Fact]
    public void Validate_VersionCompatibleTooNew_ThrowsUnsupportedException()
    {
        var builder = new TestHeaderBuilder { VersionCompatible = 0x02000000 };
        var bytes = builder.Build();
        var head = WiaFileHead.Parse(bytes);

        Assert.Throws<RvzUnsupportedException>(() => head.Validate(bytes, bytes.Length));
    }

    [Fact]
    public void Validate_FileSizeMismatch_ThrowsFormatException()
    {
        var bytes = new TestHeaderBuilder().Build();
        var head = WiaFileHead.Parse(bytes);

        Assert.Throws<RvzFormatException>(() => head.Validate(bytes, bytes.Length + 1));
    }

    [Fact]
    public void Validate_TamperedHash_ThrowsHashMismatchException()
    {
        var bytes = new TestHeaderBuilder().Build();
        bytes[10] ^= 0xFF; // inside the hashed region
        var head = WiaFileHead.Parse(bytes);

        Assert.Throws<RvzHashMismatchException>(() => head.Validate(bytes, bytes.Length));
    }

    [Fact]
    public void Validate_ValidHeader_Passes()
    {
        var bytes = new TestHeaderBuilder().Build();
        var head = WiaFileHead.Parse(bytes);

        head.Validate(bytes, bytes.Length); // must not throw
    }

    [Fact]
    public void FormatVersion_MatchesDolphinStyle()
    {
        Assert.Equal("1.00", WiaFileHead.FormatVersion(0x01000000));
        Assert.Equal("0.03", WiaFileHead.FormatVersion(0x00030000));
    }
}
