using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class WbfsBlobTests
{
    // Real WBFS files use 2 MiB clusters (wlba entries are u16, so small clusters would
    // overflow for a full disc).
    private const int ClusterSize = 0x200000;

    [Fact]
    public void RoundTrip_FullAndEmptyClusters()
    {
        var iso = new byte[2 * ClusterSize + 0x1234];
        new Random(41).NextBytes(iso);

        var wbfs = TestLegacyBuilders.BuildWbfs(iso, ClusterSize);
        using var reader = WbfsBlob.Open(new MemoryStream(wbfs));
        Assert.Equal(BlobType.Wbfs, reader.Type);
        Assert.Equal(WbfsBlob.WiiDataSize, reader.Length);

        // The ISO prefix round-trips; the (empty) tail decodes to zeroes.
        var probe = new byte[iso.Length + ClusterSize];
        reader.ReadAt(0, probe);

        var expected = new byte[probe.Length];
        iso.CopyTo(expected, 0);
        Assert.Equal(expected, probe);
    }

    [Fact]
    public void SparseDisc_EmptyClustersZeroFilled()
    {
        // The ISO covers only the first cluster; the rest map to the shared zero cluster.
        var iso = new byte[ClusterSize];
        new Random(42).NextBytes(iso);

        var wbfs = TestLegacyBuilders.BuildWbfs(iso, ClusterSize);
        using var reader = WbfsBlob.Open(new MemoryStream(wbfs));

        var probe = new byte[2 * ClusterSize];
        reader.ReadAt(0, probe);

        var expected = new byte[2 * ClusterSize];
        iso.CopyTo(expected, 0);
        Assert.Equal(expected, probe);
    }

    [Fact]
    public void RandomAccess()
    {
        var iso = new byte[2 * ClusterSize];
        new Random(43).NextBytes(iso);
        var wbfs = TestLegacyBuilders.BuildWbfs(iso, ClusterSize);

        using var reader = WbfsBlob.Open(new MemoryStream(wbfs));
        var probe = new byte[0x10000];
        reader.ReadAt(ClusterSize + 0x8000, probe);
        Assert.Equal(iso.AsSpan(ClusterSize + 0x8000, 0x10000).ToArray(), probe);
    }

    [Fact]
    public void BadMagic_ThrowsFormatException()
    {
        var bytes = new byte[512];
        Assert.Throws<RvzFormatException>(() => WbfsBlob.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void SizeMismatch_ThrowsFormatException()
    {
        var iso = new byte[ClusterSize];
        var wbfs = TestLegacyBuilders.BuildWbfs(iso, ClusterSize);
        var trimmed = wbfs.AsSpan(0, wbfs.Length - 512).ToArray();

        Assert.Throws<RvzFormatException>(() => WbfsBlob.Open(new MemoryStream(trimmed)));
    }

    [Fact]
    public void DiscTable_IsReadFromOffset12_NotPadding()
    {
        // disc_table[0] lives at byte 12 (after magic/count/shifts + 2 padding bytes,
        // WbfsBlob.h); a byte at the padding offset must not be mistaken for a disc.
        var iso = new byte[ClusterSize];
        var wbfs = TestLegacyBuilders.BuildWbfs(iso, ClusterSize);
        Assert.Equal(1, wbfs[12]); // the builder writes the disc marker at offset 12
        Assert.Equal(0, wbfs[10]); // and the padding stays clear

        // A file whose slot-0 marker is missing at offset 12 is rejected, even if the
        // padding byte at offset 10 is set (the pre-fix bug accepted such files).
        wbfs[10] = 1;
        wbfs[12] = 0;
        Assert.Throws<RvzFormatException>(() => WbfsBlob.Open(new MemoryStream(wbfs)));
    }

    [Fact]
    public void SplitFiles_RoundTrip()
    {
        // game.wbfs + game.wbf1 continuation files (Dolphin: WbfsBlob.cpp:32-33, 62-79):
        // the declared size is checked against the SUM of the parts, and reads span them.
        var iso = new byte[2 * ClusterSize + 0x1234];
        new Random(44).NextBytes(iso);
        var wbfs = TestLegacyBuilders.BuildWbfs(iso, ClusterSize);

        var dir = Path.Combine(Path.GetTempPath(), "rvzsharp-test-wbfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var part0 = Path.Combine(dir, "game.wbfs");
        var part1 = Path.Combine(dir, "game.wbf1");
        var split = wbfs.Length / 2;
        File.WriteAllBytes(part0, wbfs[..split]);
        File.WriteAllBytes(part1, wbfs[split..]);
        try
        {
            using var file = File.OpenRead(part0);
            using var blob = Blob.Open(file, filePath: part0, leaveOpen: true);
            Assert.Equal(BlobType.Wbfs, blob.Type);

            var probe = new byte[iso.Length + ClusterSize];
            Assert.Equal(probe.Length, blob.ReadAt(0, probe));
            var expected = new byte[probe.Length];
            iso.CopyTo(expected, 0);
            Assert.Equal(expected, probe);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
