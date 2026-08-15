using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class BlobDetectionTests
{
    [Fact]
    public void DetectsEveryMagic()
    {
        var iso = new byte[0x10000];
        var rng = new Random(1);
        rng.NextBytes(iso);

        var rvz = TestRvzBuilder.Build(new RvzSpec { Compression = CompressionType.Zstd, RawSize = 0x8000 });
        var wia = TestRvzBuilder.Build(new RvzSpec { IsWia = true, Compression = CompressionType.Bzip2, RawSize = 0x8000 });
        var gcz = TestLegacyBuilders.BuildGcz(iso);
        var ciso = TestLegacyBuilders.BuildCiso(iso, 0x8000, [0, 1]);
        var wbfs = TestLegacyBuilders.BuildWbfs(iso);
        var (tgc, _) = TestLegacyBuilders.BuildTgc();
        var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 3, [(0, 2)]);

        var cases = new (byte[] Bytes, BlobType Type)[]
        {
            (rvz, BlobType.Rvz),
            (wia, BlobType.Wia),
            (gcz, BlobType.Gcz),
            (ciso, BlobType.Ciso),
            (wbfs, BlobType.Wbfs),
            (tgc, BlobType.Tgc),
            (nfs, BlobType.Nfs),
            (iso, BlobType.Plain)
        };

        foreach (var (bytes, type) in cases)
        {
            using var reader = type == BlobType.Nfs
                ? Blob.Open(new MemoryStream(bytes), key, leaveOpen: true)
                : Blob.Open(new MemoryStream(bytes), leaveOpen: true);
            Assert.Equal(type, reader.Type);
        }
    }

    [Fact]
    public void ShortFile_ThrowsFormatException()
    {
        Assert.Throws<RvzFormatException>(() => Blob.Open(new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public void NonSeekableStream_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Blob.Open(new NonSeekableStream()));
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
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
