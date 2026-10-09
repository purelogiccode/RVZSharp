using System.Buffers.Binary;
using RVZSharp.Compression;
using RVZSharp.IO;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the low-level helpers: <see cref="SpanReader"/> (big-endian container reads),
/// <see cref="Crc32"/>, <see cref="ParallelExecution"/> (original-exception preservation),
/// <see cref="CodecErrorStream"/> (codec failures become <see cref="RvzFormatException"/>)
/// and <see cref="MultiPartStream"/> (split-image concatenation).
/// </summary>
public class IoUtilityTests
{
    /// <summary>Verifies that the span reader decodes the container's big-endian fields.</summary>
    [Fact]
    public void SpanReader_ReadsBigEndianFields()
    {
        var data = new byte[1 + 2 + 4 + 4 + 8 + 3];
        data[0] = 0xAB;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), 0x1234);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(3), 0x89ABCDEF);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(7), -2);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(11), 0x1122334455667788);
        data[19] = 0x01;
        data[20] = 0x02;
        data[21] = 0x03;

        var reader = new SpanReader(data);

        Assert.Equal((byte)0xAB, reader.ReadByte());
        Assert.Equal((ushort)0x1234, reader.ReadUInt16());
        Assert.Equal(0x89ABCDEFu, reader.ReadUInt32());
        Assert.Equal(-2, reader.ReadInt32());
        Assert.Equal(0x1122334455667788UL, reader.ReadUInt64());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBytes(3).ToArray());
        Assert.Equal(data.Length, reader.Position);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Verifies that each read fails cleanly when fewer bytes remain.</summary>
    [Fact]
    public void SpanReader_ShortData_Throws()
    {
        Assert.Throws<RvzFormatException>(() => new SpanReader(ReadOnlySpan<byte>.Empty).ReadByte());
        Assert.Throws<RvzFormatException>(() => new SpanReader(ReadOnlySpan<byte>.Empty).ReadUInt16());
        Assert.Throws<RvzFormatException>(() => new SpanReader(ReadOnlySpan<byte>.Empty).ReadUInt32());
        Assert.Throws<RvzFormatException>(() => new SpanReader(ReadOnlySpan<byte>.Empty).ReadInt32());
        Assert.Throws<RvzFormatException>(() => new SpanReader(ReadOnlySpan<byte>.Empty).ReadUInt64());
        Assert.Throws<RvzFormatException>(() => new SpanReader(ReadOnlySpan<byte>.Empty).ReadBytes(1));

        var one = new SpanReader(new byte[1]);
        Assert.Equal(1, one.Remaining);
        one.ReadByte();
        Assert.Equal(0, one.Remaining);
    }

    /// <summary>Verifies the IEEE CRC-32 check value for "123456789".</summary>
    [Fact]
    public void Crc32_MatchesKnownCheckValue()
    {
        var data = "123456789"u8.ToArray();
        var crc = Crc32.Update(0xFFFFFFFF, data) ^ 0xFFFFFFFF;
        Assert.Equal(0xCBF43926u, crc);
    }

    /// <summary>Verifies that incremental updates match a single update and an empty update is a no-op.</summary>
    [Fact]
    public void Crc32_Incremental_MatchesOneShot()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var oneShot = Crc32.Update(0xFFFFFFFF, data);

        var running = 0xFFFFFFFFu;
        for (var i = 0; i < data.Length; i += 7)
        {
            running = Crc32.Update(running, data.AsSpan(i, Math.Min(7, data.Length - i)));
        }

        Assert.Equal(oneShot, running);
        Assert.Equal(running, Crc32.Update(running, ReadOnlySpan<byte>.Empty));
    }

    /// <summary>Verifies that ParallelExecution rethrows the loop body's original exception.</summary>
    [Fact]
    public void ParallelExecution_For_PropagatesOriginalException()
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = 2 };
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ParallelExecution.For(0, 8, options, i =>
            {
                if (i == 3)
                {
                    throw new InvalidOperationException("boom");
                }
            }));

        Assert.Equal("boom", exception.Message);
    }

    /// <summary>Verifies that the thread-local overload rethrows the original exception too.</summary>
    [Fact]
    public void ParallelExecution_ForLocal_PropagatesOriginalException()
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = 2 };
        var exception = Assert.Throws<RvzFormatException>(() =>
            ParallelExecution.For(
                0,
                8,
                options,
                () => 0,
                (i, _, state) =>
                {
                    if (i == 5)
                    {
                        throw new RvzFormatException("bad data");
                    }

                    return state;
                },
                _ => { }));

        Assert.Equal("bad data", exception.Message);
    }

    /// <summary>Verifies that ParallelExecution observes cancellation.</summary>
    [Fact]
    public void ParallelExecution_For_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var options = new ParallelOptions { CancellationToken = cts.Token };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ParallelExecution.For(0, 4, options, _ => { }));
    }

    /// <summary>Verifies that ParallelExecution runs every iteration.</summary>
    [Fact]
    public void ParallelExecution_For_RunsAllIterations()
    {
        var results = new int[64];
        ParallelExecution.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            i => results[i] = i);

        Assert.Equal(Enumerable.Range(0, 64), results);
    }

    /// <summary>Verifies that a factory failure becomes an RvzFormatException naming the codec.</summary>
    [Fact]
    public void CodecErrorStream_Create_FactoryFailure_Wraps()
    {
        var exception = Assert.Throws<RvzFormatException>(() =>
            CodecErrorStream.Create(() => throw new InvalidOperationException("bad header"), "Zstd"));
        Assert.Contains("Zstd", exception.Message);
        Assert.Contains("bad header", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    /// <summary>Verifies that an RvzException from the factory is rethrown unchanged.</summary>
    [Fact]
    public void CodecErrorStream_Create_RvzException_Propagates()
    {
        var original = new RvzFormatException("already mapped");
        var exception = Assert.Throws<RvzFormatException>(() =>
            CodecErrorStream.Create(() => throw original, "LZMA2"));
        Assert.Same(original, exception);
    }

    /// <summary>Verifies that I/O, cancellation and disposed-object exceptions are not wrapped.</summary>
    [Fact]
    public void CodecErrorStream_Create_UnwrappedExceptions_Propagate()
    {
        Assert.Throws<IOException>(() =>
            CodecErrorStream.Create(() => throw new IOException("io"), "BZip2"));
        Assert.Throws<OperationCanceledException>(() =>
            CodecErrorStream.Create(() => throw new OperationCanceledException(), "BZip2"));
        Assert.Throws<ObjectDisposedException>(() =>
            CodecErrorStream.Create(() => throw new ObjectDisposedException("inner"), "BZip2"));
    }

    /// <summary>Verifies that decoder failures during reads are mapped to RvzFormatException.</summary>
    [Fact]
    public void CodecErrorStream_ReadFailures_AreMapped()
    {
        using var read = CodecErrorStream.Create(
            () => new FaultyStream { ReadException = new IndexOutOfRangeException("corrupt") }, "LZMA");
        Assert.Contains("LZMA", Assert.Throws<RvzFormatException>(
            () => read.Read(new byte[4], 0, 4)).Message);

        using var span = CodecErrorStream.Create(
            () => new FaultyStream { ReadException = new InvalidDataException("corrupt") }, "LZMA");
        Assert.Throws<RvzFormatException>(() => span.Read(new byte[4].AsSpan()));

        using var readByte = CodecErrorStream.Create(
            () => new FaultyStream { ReadException = new IndexOutOfRangeException("corrupt") }, "LZMA");
        Assert.Throws<RvzFormatException>(() => readByte.ReadByte());
    }

    /// <summary>Verifies that I/O failures during reads are not wrapped.</summary>
    [Fact]
    public void CodecErrorStream_ReadIOException_Propagates()
    {
        using var stream = CodecErrorStream.Create(
            () => new FaultyStream { ReadException = new IOException("disk") }, "Zstd");
        Assert.Throws<IOException>(() => stream.Read(new byte[4], 0, 4));
    }

    /// <summary>Verifies the pass-through members and the read-only contract.</summary>
    [Fact]
    public void CodecErrorStream_PassThroughMembers()
    {
        using var inner = new MemoryStream(new byte[16]);
        using var stream = CodecErrorStream.Create(() => inner, "None");

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(16, stream.Length);

        stream.Position = 3;
        Assert.Equal(3, stream.Position);
        Assert.Equal(3, stream.Seek(3, SeekOrigin.Begin));
        stream.Flush();

        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
    }

    /// <summary>Verifies that disposing the wrapper disposes the inner codec stream.</summary>
    [Fact]
    public void CodecErrorStream_Dispose_DisposesInner()
    {
        var inner = new TrackingStream();
        var stream = CodecErrorStream.Create(() => inner, "None");
        stream.Dispose();
        Assert.True(inner.Disposed);
    }

    /// <summary>Verifies that MultiPartStream concatenates parts and reads across boundaries.</summary>
    [Fact]
    public void MultiPartStream_ReadsAcrossParts()
    {
        using var first = new MemoryStream(Enumerable.Range(0, 10).Select(i => (byte)i).ToArray());
        using var second = new MemoryStream(Enumerable.Range(10, 10).Select(i => (byte)i).ToArray());
        using var stream = new MultiPartStream([first, second], leaveOpen: true);

        Assert.Equal(20, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);

        var all = new byte[20];
        Assert.Equal(20, stream.Read(all, 0, all.Length));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), all);
        Assert.Equal(20, stream.Position);
        Assert.Equal(0, stream.Read(all, 0, all.Length)); // at the end

        stream.Seek(-6, SeekOrigin.End);
        Assert.Equal(14, stream.Position);
        var tail = new byte[10];
        Assert.Equal(6, stream.Read(tail, 0, tail.Length));
        Assert.Equal(Enumerable.Range(14, 6).Select(i => (byte)i), tail.Take(6));
    }

    /// <summary>Verifies the seek origins and the read-only contract.</summary>
    [Fact]
    public void MultiPartStream_SeekAndReadOnlyContract()
    {
        using var first = new MemoryStream(new byte[4]);
        using var second = new MemoryStream(new byte[4]);
        using var stream = new MultiPartStream([first, second], leaveOpen: true);

        Assert.Equal(2, stream.Seek(2, SeekOrigin.Begin));
        Assert.Equal(5, stream.Seek(3, SeekOrigin.Current));
        Assert.Equal(6, stream.Seek(-2, SeekOrigin.End));
        stream.Flush();
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
    }

    /// <summary>Verifies that disposing the stream disposes the parts unless leaveOpen is set.</summary>
    [Fact]
    public void MultiPartStream_Dispose_RespectsLeaveOpen()
    {
        var first = new TrackingStream();
        var second = new TrackingStream();
        using (var stream = new MultiPartStream([first, second], leaveOpen: true))
        {
        }

        Assert.False(first.Disposed);
        Assert.True(second.Disposed);

        var owned = new TrackingStream();
        var other = new TrackingStream();
        using (var stream = new MultiPartStream([owned, other], leaveOpen: false))
        {
        }

        Assert.True(owned.Disposed);
        Assert.True(other.Disposed);
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FaultyStream : Stream
    {
        public Exception? ReadException { get; init; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw ReadException ?? new IOException("no exception configured");
        }

        public override int Read(Span<byte> buffer)
        {
            throw ReadException ?? new IOException("no exception configured");
        }

        public override int ReadByte()
        {
            throw ReadException ?? new IOException("no exception configured");
        }

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
