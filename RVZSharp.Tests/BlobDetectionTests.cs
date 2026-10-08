using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>Unit tests for blob detection.</summary>
public class BlobDetectionTests
{
    /// <summary>Verifies that detects every magic.</summary>
    [Fact]
    public void DetectsEveryMagic()
    {
        var iso = new byte[0x10000];
        var rng = new Random(1);
        rng.NextBytes(iso);

        var rvz = TestRvzBuilder.Build(new RvzSpec { Compression = CompressionType.Zstd, RawSize = 0x8000 });
        var wia = TestRvzBuilder.Build(new RvzSpec
            { IsWia = true, Compression = CompressionType.Bzip2, RawSize = 0x8000 });
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

    /// <summary>Verifies that short file throws format exception.</summary>
    [Fact]
    public void ShortFile_ThrowsFormatException()
    {
        Assert.Throws<RvzFormatException>(() => Blob.Open(new MemoryStream([1, 2, 3])));
    }

    /// <summary>Verifies that non seekable stream throws argument exception.</summary>
    [Fact]
    public void NonSeekableStream_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Blob.Open(new NonSeekableStream()));
    }

    /// <summary>Verifies that corrupt container with real magic throws format exception.</summary>
    [Theory]
    [InlineData("RVZ\x01")]
    [InlineData("WIA\x01")]
    [InlineData("WBFS")]
    public void CorruptContainer_WithRealMagic_ThrowsFormatException(string magic)
    {
        // A file that starts with a recognized container magic must be parsed as that
        // container — a parse failure is a format error, never a silent PlainBlob fallback
        // (the fallback is only for files with no recognizable magic).
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(content, 0);

        Assert.ThrowsAny<RvzException>(() => Blob.Open(new MemoryStream(content), leaveOpen: true));
    }

    /// <summary>Verifies that corrupt CISO opens but writer rejects decoded garbage.</summary>
    [Fact]
    public void CorruptCiso_OpensButWriterRejectsDecodedGarbage()
    {
        // CISO is validated lazily (like Dolphin): a header with a plausible block size
        // opens even though the payload is garbage — the decoded bytes carry no disc magic.
        // The writer's disc-header validation is what stops such garbage from being wrapped
        // into an RVZ.
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        System.Text.Encoding.ASCII.GetBytes("CISO").CopyTo(content, 0);
        content[4] = 0x00; // block size 0x8000, little endian
        content[5] = 0x80;
        content[6] = 0x00;
        content[7] = 0x00;

        using var blob = Blob.Open(new MemoryStream(content), leaveOpen: true);
        Assert.Equal(BlobType.Ciso, blob.Type);
        Assert.Throws<RvzFormatException>(() =>
        {
            using var output = new MemoryStream();
            RvzWriter.Write(blob, output, RvzWriteOptions.Default);
        });
    }

    /// <summary>Verifies that corrupt GCZ with real magic throws format exception.</summary>
    [Fact]
    public void CorruptGcz_WithRealMagic_ThrowsFormatException()
    {
        // The real GCZ magic is 0xB10BC001 little endian (Dolphin: GCZ_MAGIC) — NOT the
        // ASCII "GCZ\0" the name suggests.
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        new byte[] { 0x01, 0xC0, 0x0B, 0xB1 }.CopyTo(content, 0);

        Assert.Throws<RvzFormatException>(() => Blob.Open(new MemoryStream(content), leaveOpen: true));
    }

    /// <summary>Verifies that corrupt TGC with real magic throws format exception.</summary>
    [Fact]
    public void CorruptTgc_WithRealMagic_ThrowsFormatException()
    {
        // TGC's magic (0xA2380FAE) is the one little-endian field in the header.
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        new byte[] { 0xAE, 0x0F, 0x38, 0xA2 }.CopyTo(content, 0);

        Assert.Throws<RvzFormatException>(() => Blob.Open(new MemoryStream(content), leaveOpen: true));
    }

    /// <summary>Verifies that fake container magic falls back to plain blob.</summary>
    [Theory]
    [InlineData("GCZ\0")]
    [InlineData("RVZ\0")]
    [InlineData("WIA\0")]
    public void FakeContainerMagic_FallsBackToPlainBlob(string magic)
    {
        // Only the REAL magics select a container; an ASCII look-alike ("GCZ\0", "RVZ\0")
        // is just arbitrary bytes and falls back to PlainBlob like any other unrecognized
        // file. The writer's disc-header validation is what rejects such inputs.
        var content = new byte[350_000];
        new Random(42).NextBytes(content);
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(content, 0);

        using var reader = Blob.Open(new MemoryStream(content), leaveOpen: true);
        Assert.Equal(BlobType.Plain, reader.Type);
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
