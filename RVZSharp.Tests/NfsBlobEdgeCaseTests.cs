using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Edge-case tests for <see cref="NfsBlob"/>: header validation, single-file vs multi-file
/// mode, the on-disk key lookup, reads across block boundaries and disposal ownership.
/// </summary>
public class NfsBlobEdgeCaseTests
{
    private static byte[] MakeKey()
    {
        return Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
    }

    /// <summary>Verifies that an LBA range wrapping the 32-bit address space is rejected.</summary>
    [Fact]
    public void Open_RangeWrap_Throws()
    {
        var header = BuildHeader(start: 0xFFFFFFFF, num: 2);
        var exception = Assert.Throws<RvzFormatException>(() =>
            NfsBlob.Open(new MemoryStream(header), MakeKey()));
        Assert.Contains("wraps past", exception.Message);
    }

    /// <summary>Verifies that a path-mode image needing a continuation file fails when it is missing.</summary>
    [Fact]
    public void Open_MissingContinuationFile_Throws()
    {
        WithContentDirectory(directory =>
        {
            var header = BuildHeader(start: 0, num: 0x100000); // ~32 GiB => many continuation files
            var path = Path.Combine(directory, "content", "hif_000000.nfs");
            var exception = Assert.Throws<RvzFormatException>(() =>
                NfsBlob.Open(new MemoryStream(header), path));
            Assert.Contains("continuation", exception.Message);
        });
    }

    /// <summary>Verifies that the files summing to less than the expected raw size are rejected.</summary>
    [Fact]
    public void Open_FilesTooSmall_Throws()
    {
        WithContentDirectory(directory =>
        {
            var header = BuildHeader(start: 0, num: 2);
            var path = Path.Combine(directory, "content", "hif_000000.nfs");
            var exception = Assert.Throws<RvzFormatException>(() =>
                NfsBlob.Open(new MemoryStream(header), path));
            Assert.Contains("at least", exception.Message);
        });
    }

    /// <summary>Verifies that an on-disk key shorter than 16 bytes is rejected.</summary>
    [Fact]
    public void Open_ShortKeyFile_Throws()
    {
        WithContentDirectory(directory =>
        {
            File.WriteAllBytes(Path.Combine(directory, "code", "htk.bin"), new byte[8]);
            var path = Path.Combine(directory, "content", "hif_000000.nfs");
            var exception = Assert.Throws<RvzFormatException>(() =>
                NfsBlob.Open(new MemoryStream(BuildHeader(0, 1)), path));
            Assert.Contains("shorter than", exception.Message);
        });
    }

    /// <summary>Verifies that opening without a path or key is unsupported.</summary>
    [Fact]
    public void Open_NoPathOrKey_Throws()
    {
        Assert.Throws<RvzUnsupportedException>(() => NfsBlob.Open(new MemoryStream(BuildHeader(0, 1))));
    }

    /// <summary>Verifies that out-of-range reads are clamped to zero bytes.</summary>
    [Fact]
    public void ReadAt_OutOfRange_ReturnsZero()
    {
        var key = MakeKey();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 2, new (uint, uint)[] { (0, 2) });
        using var blob = NfsBlob.Open(new MemoryStream(nfs), key);

        var buffer = new byte[16];
        Assert.Equal(0, blob.ReadAt(-1, buffer));
        Assert.Equal(0, blob.ReadAt(blob.Length, buffer));
        Assert.Equal(0, blob.ReadAt(blob.Length + 100, buffer));
        Assert.Equal(0, blob.ReadAt(0, Span<byte>.Empty));
    }

    /// <summary>Verifies that a read crossing a block boundary matches the decoded image.</summary>
    [Fact]
    public void ReadAt_AcrossBlockBoundary_MatchesImage()
    {
        var key = MakeKey();
        var (nfs, iso) = TestLegacyBuilders.BuildNfs(key, 3, new (uint, uint)[] { (0, 3) });
        using var blob = NfsBlob.Open(new MemoryStream(nfs), key);

        var buffer = new byte[16];
        Assert.Equal(16, blob.ReadAt(0x7FF8, buffer));
        Assert.Equal(iso[0x7FF8..0x8008], buffer);

        var last = new byte[1];
        Assert.Equal(1, blob.ReadAt(blob.Length - 1, last));
        Assert.Equal(iso[^1], last[0]);
    }

    /// <summary>Verifies the reported type and block size.</summary>
    [Fact]
    public void TypeAndBlockSize_AreNfsDefaults()
    {
        var key = MakeKey();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 1, new (uint, uint)[] { (0, 1) });
        using var blob = NfsBlob.Open(new MemoryStream(nfs), key);

        Assert.Equal(BlobType.Nfs, blob.Type);
        Assert.Equal(0x8000, blob.BlockSize);
    }

    /// <summary>Verifies that disposal follows the leaveOpen flag.</summary>
    [Fact]
    public void Dispose_RespectsLeaveOpen()
    {
        var key = MakeKey();
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, 1, new (uint, uint)[] { (0, 1) });

        var kept = new TrackingStream(nfs);
        NfsBlob.Open(kept, key, leaveOpen: true).Dispose();
        Assert.False(kept.Disposed);

        var owned = new TrackingStream(nfs);
        NfsBlob.Open(owned, key, leaveOpen: false).Dispose();
        Assert.True(owned.Disposed);
    }

    /// <summary>Builds a 0x200-byte NFS header with one LBA range.</summary>
    private static byte[] BuildHeader(uint start, uint num)
    {
        var header = new byte[0x200];
        "EGGS"u8.CopyTo(header);
        WriteBe32(header, 0x10, 1);
        WriteBe32(header, 0x14, start);
        WriteBe32(header, 0x18, num);
        return header;
    }

    private static void WithContentDirectory(Action<string> action)
    {
        var directory = Directory.CreateTempSubdirectory("rvzsharp-nfs-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "content"));
            Directory.CreateDirectory(Path.Combine(directory, "code"));
            File.WriteAllBytes(Path.Combine(directory, "code", "htk.bin"), MakeKey());
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
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
}
