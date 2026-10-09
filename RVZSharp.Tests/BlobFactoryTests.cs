using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the <see cref="Blob"/> factory: magic sniffing, split plain ISOs
/// (<c>game.part0.iso</c> + continuations), stream ownership, the NFS key overload and the
/// blob-type names.
/// </summary>
public class BlobFactoryTests
{
    /// <summary>Verifies that a file too short for a magic number throws.</summary>
    [Fact]
    public void Open_TooShort_Throws()
    {
        Assert.Throws<RvzFormatException>(() => Blob.Open(new MemoryStream(new byte[3])));
    }

    /// <summary>Verifies that a non-seekable stream is rejected.</summary>
    [Fact]
    public void Open_NonSeekable_Throws()
    {
        using var stream = new NonSeekableStream(new byte[16]);
        Assert.Throws<ArgumentException>(() => Blob.Open(stream));
        Assert.Throws<ArgumentException>(() => Blob.Open(stream, new byte[16]));
    }

    /// <summary>Verifies that data without a container magic opens as a plain ISO.</summary>
    [Fact]
    public void Open_UnknownMagic_PlainBlob()
    {
        var data = new byte[0x200];
        new Random(1).NextBytes(data);
        using var blob = Blob.Open(new MemoryStream(data));
        Assert.Equal(BlobType.Plain, blob.Type);
    }

    /// <summary>Verifies that leaveOpen keeps the caller's stream open on dispose.</summary>
    [Fact]
    public void Open_LeaveOpen_KeepsStreamOpen()
    {
        var data = new byte[0x200];
        var stream = new MemoryStream(data);
        using (var blob = Blob.Open(stream, leaveOpen: true))
        {
            Assert.Equal(data.Length, blob.Length);
        }

        Assert.True(stream.CanRead);
        stream.Dispose();
    }

    /// <summary>Verifies that the reader disposes the stream when leaveOpen is false.</summary>
    [Fact]
    public void Open_NotLeaveOpen_DisposesStream()
    {
        var stream = new TrackingStream(new byte[0x200]);
        using (Blob.Open(stream))
        {
        }

        Assert.True(stream.Disposed);
    }

    /// <summary>Verifies that a split plain ISO concatenates its continuation parts.</summary>
    [Fact]
    public void Open_SplitPlainIso_ConcatenatesParts()
    {
        WithTempDirectory(directory =>
        {
            var part0 = Path.Combine(directory, "game.part0.iso");
            var part1 = Path.Combine(directory, "game.part1.iso");
            var part2 = Path.Combine(directory, "game.part2.iso");
            var expected = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(part0, expected[..10]);
            File.WriteAllBytes(part1, expected[10..16]);
            File.WriteAllBytes(part2, expected[16..]);

            using var blob = Blob.Open(File.OpenRead(part0), part0);
            Assert.Equal(BlobType.Plain, blob.Type);
            Assert.Equal(20, blob.Length);

            var all = new byte[20];
            Assert.Equal(20, blob.ReadAt(0, all));
            Assert.Equal(expected, all);

            // Reads across the part boundaries.
            var middle = new byte[8];
            Assert.Equal(8, blob.ReadAt(6, middle));
            Assert.Equal(expected[6..14], middle);
        });
    }

    /// <summary>Verifies that a zero-size continuation makes the image fall back to the first part.</summary>
    [Fact]
    public void Open_SplitPlainIso_ZeroSizeContinuation_FallsBack()
    {
        WithTempDirectory(directory =>
        {
            var part0 = Path.Combine(directory, "game.part0.iso");
            var part1 = Path.Combine(directory, "game.part1.iso");
            File.WriteAllBytes(part0, new byte[10]);
            File.WriteAllBytes(part1, []);

            using var blob = Blob.Open(File.OpenRead(part0), part0);
            Assert.Equal(10, blob.Length);
        });
    }

    /// <summary>Verifies that a missing continuation leaves the first part alone.</summary>
    [Fact]
    public void Open_SplitPlainIso_MissingContinuation_FallsBack()
    {
        WithTempDirectory(directory =>
        {
            var part0 = Path.Combine(directory, "game.part0.iso");
            File.WriteAllBytes(part0, new byte[10]);

            using var blob = Blob.Open(File.OpenRead(part0), part0);
            Assert.Equal(10, blob.Length);
        });
    }

    /// <summary>Verifies that opening by path owns the file stream.</summary>
    [Fact]
    public void Open_Path_OwnsStream()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "image.iso");
            File.WriteAllBytes(path, new byte[0x200]);

            using (var blob = Blob.Open(path))
            {
                Assert.Equal(BlobType.Plain, blob.Type);
            }

            File.Delete(path); // the reader closed the stream
        });
    }

    /// <summary>Verifies that the explicit-key overload opens an NFS image.</summary>
    [Fact]
    public void Open_NfsKeyOverload_OpensNfs()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 4, new (uint, uint)[] { (0, 4) });

        using var blob = Blob.Open(new MemoryStream(nfs), key);
        Assert.Equal(BlobType.Nfs, blob.Type);
        Assert.Equal(4 * 0x8000, blob.Length);
    }

    /// <summary>Verifies that the key overload ignores the key for non-NFS formats.</summary>
    [Fact]
    public void Open_StreamKeyOverload_NonNfs_IgnoresKey()
    {
        var data = new byte[0x200];
        using var blob = Blob.Open(new MemoryStream(data), new byte[16]);
        Assert.Equal(BlobType.Plain, blob.Type);
    }

    /// <summary>Verifies the human-readable names of every blob type.</summary>
    /// <param name="type">The blob type.</param>
    /// <param name="expected">The expected display name.</param>
    [Theory]
    [InlineData(BlobType.Plain, "ISO")]
    [InlineData(BlobType.Gcz, "GCZ")]
    [InlineData(BlobType.Ciso, "CISO")]
    [InlineData(BlobType.Wbfs, "WBFS")]
    [InlineData(BlobType.Tgc, "TGC")]
    [InlineData(BlobType.Wia, "WIA")]
    [InlineData(BlobType.Rvz, "RVZ")]
    [InlineData(BlobType.Nfs, "NFS")]
    public void GetName_MapsBlobType(BlobType type, string expected)
    {
        Assert.Equal(expected, Blob.GetName(type));
    }

    /// <summary>Verifies that the disc-type helpers reject a null reader.</summary>
    [Fact]
    public void GetDiscTypeAndIsDisc_Null_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Blob.GetDiscType(null!));
        Assert.Throws<ArgumentNullException>(() => Blob.IsDisc(null!));
    }

    private static void WithTempDirectory(Action<string> action)
    {
        var directory = Directory.CreateTempSubdirectory("rvzsharp-blob-").FullName;
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
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
