using RVZSharp.IO;

namespace RVZSharp.Tests;

/// <summary>Unit tests for section stream.</summary>
public class SectionStreamTests
{
    /// <summary>Verifies that external seek does not read outside section.</summary>
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

    /// <summary>Verifies that reads are bounded to section end.</summary>
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

    /// <summary>Verifies that a section outside the base stream is rejected.</summary>
    [Fact]
    public void Constructor_OutsideStream_Throws()
    {
        using var baseStream = new MemoryStream(new byte[0x100]);

        Assert.Throws<RvzFormatException>(() => new SectionStream(baseStream, -1, 4));
        Assert.Throws<RvzFormatException>(() => new SectionStream(baseStream, 0, -1));
        Assert.Throws<RvzFormatException>(() => new SectionStream(baseStream, 0xF0, 0x20));
    }

    /// <summary>Verifies that the position setter validates the section bounds.</summary>
    [Fact]
    public void Position_ValidatesBounds()
    {
        using var baseStream = new MemoryStream(new byte[0x100]);
        using var section = new SectionStream(baseStream, 0x40, 0x20);

        Assert.Equal(0, section.Position);
        section.Position = 0x10;
        Assert.Equal(0x10, section.Position);
        section.Position = section.Length;
        Assert.Equal(0x20, section.Position);

        Assert.Throws<ArgumentOutOfRangeException>(() => section.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => section.Position = section.Length + 1);
    }

    /// <summary>Verifies the seek origins and that an invalid origin is rejected.</summary>
    [Fact]
    public void Seek_UsesOrigins()
    {
        using var baseStream = new MemoryStream(new byte[0x100]);
        using var section = new SectionStream(baseStream, 0x40, 0x20);

        Assert.Equal(0x10, section.Seek(0x10, SeekOrigin.Begin));
        Assert.Equal(0x14, section.Seek(4, SeekOrigin.Current));
        Assert.Equal(0x18, section.Seek(-8, SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => section.Seek(0, (SeekOrigin)99));
    }

    /// <summary>Verifies the read-only contract and the no-op flush.</summary>
    [Fact]
    public void ReadOnlyContract()
    {
        using var baseStream = new MemoryStream(new byte[0x100]);
        using var section = new SectionStream(baseStream, 0, 0x10);

        Assert.True(section.CanRead);
        Assert.True(section.CanSeek);
        Assert.False(section.CanWrite);
        Assert.Equal(0x10, section.Length);
        section.Flush();
        Assert.Throws<NotSupportedException>(() => section.SetLength(1));
        Assert.Throws<NotSupportedException>(() => section.Write(new byte[1], 0, 1));
    }

    /// <summary>Verifies that the byte-array read overload is clamped to the section end.</summary>
    [Fact]
    public void Read_ByteArrayOverload_ClampsToSectionEnd()
    {
        using var baseStream = new MemoryStream(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        using var section = new SectionStream(baseStream, 0x80, 0x10);

        section.Seek(8, SeekOrigin.Begin);
        var buffer = new byte[0x20];
        Assert.Equal(8, section.Read(buffer, 0, buffer.Length));
        Assert.Equal(Enumerable.Range(0x88, 8).Select(i => (byte)i).ToArray(), buffer[..8]);
        Assert.Equal(0, section.Read(buffer, 0, buffer.Length)); // at the end
    }
}
