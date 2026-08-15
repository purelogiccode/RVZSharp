using RVZSharp.IO;

namespace RVZSharp.Tests;

public class NonDisposingStreamTests
{
    private static MemoryStream CreateInner(out byte[] data)
    {
        data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        return new MemoryStream(data);
    }

    [Fact]
    public void Dispose_DoesNotDisposeTheInnerStream()
    {
        using var inner = CreateInner(out _);
        var wrapper = new NonDisposingStream(inner);

        wrapper.Dispose();

        // Still readable: the wrapper must not have closed the inner stream.
        Assert.Equal(0, inner.ReadByte());
    }

    [Fact]
    public void Read_And_ReadSpan_DelegateToTheInnerStream()
    {
        using var inner = CreateInner(out var data);
        using var wrapper = new NonDisposingStream(inner);

        var array = new byte[16];
        Assert.Equal(16, wrapper.Read(array, 0, array.Length));
        Assert.Equal(data.Take(16).ToArray(), array);

        var span = new byte[16];
        Assert.Equal(16, wrapper.Read(span));
        Assert.Equal(data.Skip(16).Take(16).ToArray(), span);
    }

    [Fact]
    public void Position_Length_Seek_Flush_Delegate()
    {
        using var inner = CreateInner(out _);
        using var wrapper = new NonDisposingStream(inner);

        Assert.Equal(0, wrapper.Position);
        Assert.Equal(256, wrapper.Length);

        wrapper.Position = 0x80;
        Assert.Equal(0x80, inner.Position);

        Assert.Equal(0x20, wrapper.Seek(0x20, SeekOrigin.Begin));

        var buffer = new byte[4];
        Assert.Equal(4, wrapper.Read(buffer));
        Assert.Equal(0x24, wrapper.Position);

        wrapper.Flush();
        Assert.Equal(0x24, inner.Position);
    }

    [Fact]
    public void Write_ThrowsNotSupported()
    {
        using var inner = CreateInner(out _);
        using var wrapper = new NonDisposingStream(inner);

        Assert.Throws<NotSupportedException>(() => wrapper.Write(new byte[1], 0, 1));
    }

    [Fact]
    public void SetLength_ThrowsNotSupported()
    {
        using var inner = CreateInner(out _);
        using var wrapper = new NonDisposingStream(inner);

        Assert.Throws<NotSupportedException>(() => wrapper.SetLength(100));
    }

    [Fact]
    public void Capabilities_MirrorTheInnerStream()
    {
        using var inner = CreateInner(out _);
        using var wrapper = new NonDisposingStream(inner);

        Assert.True(wrapper.CanRead);
        Assert.True(wrapper.CanSeek);
        Assert.False(wrapper.CanWrite);
    }
}