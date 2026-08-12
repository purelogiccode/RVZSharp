using RVZSharp;
using RVZSharp.Blobs;
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
}
