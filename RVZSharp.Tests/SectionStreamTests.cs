using RVZSharp.IO;

namespace RVZSharp.Tests;

public class SectionStreamTests
{
    [Fact]
    public void ExternalSeek_DoesNotReadOutsideSection()
    {
        // The class contract is "reads never cross the section bounds": a base stream
        // seeked externally (before or after the section) must yield 0, not data from
        // outside the section.
        using var baseStream = new MemoryStream(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        using var section = new SectionStream(baseStream, 0x80, 0x40);

        baseStream.Position = 0; // before the section
        Assert.Equal(0, section.Read(new byte[16]));

        baseStream.Position = 0x200; // past the section
        Assert.Equal(0, section.Read(new byte[16]));

        // The position getter clamps instead of reporting an out-of-section value, and
        // reads work again once the position is set back into the section.
        Assert.Equal(0x40, section.Position); // clamped to the section length
        section.Position = 0;
        var buffer = new byte[0x40];
        Assert.Equal(0x40, section.Read(buffer, 0, buffer.Length));
        Assert.Equal(Enumerable.Range(0x80, 0x40).Select(i => (byte)i).ToArray(), buffer);
    }

    [Fact]
    public void Reads_AreBoundedToSectionEnd()
    {
        using var baseStream = new MemoryStream(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        using var section = new SectionStream(baseStream, 0x40, 0x20);

        // Reading past the section end returns only the in-section bytes.
        var buffer = new byte[0x40];
        Assert.Equal(0x20, section.Read(buffer, 0, buffer.Length));
        Assert.Equal(Enumerable.Range(0x40, 0x20).Select(i => (byte)i).ToArray(), buffer[..0x20]);
        Assert.Equal(0, section.Read(new byte[1]));
    }
}
