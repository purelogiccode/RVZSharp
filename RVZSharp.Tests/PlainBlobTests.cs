using RVZSharp.Blobs;
using RVZSharp.Models;

namespace RVZSharp.Tests;

public class PlainBlobTests
{
    private static MemoryStream StreamWith(byte[] data)
    {
        return new MemoryStream(data);
    }

    private static byte[] Pattern(int length)
    {
        return Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
    }

    [Fact]
    public void Metadata_AreExposed()
    {
        using var blob = PlainBlob.Open(StreamWith(Pattern(0x100)));

        Assert.Equal(BlobType.Plain, blob.Type);
        Assert.Equal(0x100, blob.Length);
        Assert.Equal(0, blob.BlockSize);
    }

    [Fact]
    public void ReadAt_ReturnsTheRequestedBytes()
    {
        using var blob = PlainBlob.Open(StreamWith(Pattern(0x100)));

        var buffer = new byte[16];
        Assert.Equal(16, blob.ReadAt(0x80, buffer));
        Assert.Equal(Pattern(0x100).Skip(0x80).Take(16).ToArray(), buffer);
    }

    [Fact]
    public void ReadAt_PastEnd_ReturnsPartial_ThenZero()
    {
        using var blob = PlainBlob.Open(StreamWith(Pattern(0x24)));

        var buffer = new byte[16];
        Assert.Equal(16, blob.ReadAt(0, buffer));
        Assert.Equal(Pattern(0x24).Take(16).ToArray(), buffer);
        Assert.Equal(0, blob.ReadAt(0x24, buffer));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x100)]
    [InlineData(1000)]
    public void ReadAt_OutOfRange_ReturnsZero(int position)
    {
        using var blob = PlainBlob.Open(StreamWith(Pattern(0x100)));

        Assert.Equal(0, blob.ReadAt(position, new byte[16]));
    }

    [Fact]
    public void ReadAt_EmptyBuffer_ReturnsZero()
    {
        using var blob = PlainBlob.Open(StreamWith(Pattern(0x100)));

        Assert.Equal(0, blob.ReadAt(0, Span<byte>.Empty));
    }

    [Fact]
    public void ReadAt_RepositionsTheUnderlyingStream()
    {
        using var stream = StreamWith(Pattern(0x100));
        using var blob = PlainBlob.Open(stream);

        stream.Position = 0x40;
        var buffer = new byte[1];
        Assert.Equal(1, blob.ReadAt(0x5A, buffer));
        Assert.Equal(0x5A, buffer[0]);
        // The blob reads what it asked for, wherever the stream was left.
        Assert.Equal(1, blob.ReadAt(0x50, buffer));
        Assert.Equal(0x50, buffer[0]);
    }

    [Fact]
    public void Dispose_ClosesTheStream_ByDefault()
    {
        var stream = StreamWith(Pattern(0x10));
        var blob = PlainBlob.Open(stream);

        blob.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    [Fact]
    public void LeaveOpen_KeepsTheStreamUsable()
    {
        var stream = StreamWith(Pattern(0x10));
        var blob = PlainBlob.Open(stream, leaveOpen: true);

        blob.Dispose();
        Assert.Equal(0, stream.ReadByte());
        stream.Dispose();
    }

    [Fact]
    public void NonSeekableStream_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlainBlob.Open(new NonSeekableReadOnlyStream()));
    }

    private sealed class NonSeekableReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}