using System.Buffers.Binary;
using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// End-to-end <see cref="WbfsWriter"/> tests: convert a synthetic Wii ISO to a standalone WBFS
/// file and decode it back through <see cref="WbfsBlob"/>, plus header, cluster-sharing and
/// option-validation tests. The decoded image is always the fixed Wii double-layer size, so
/// tests compare the image prefix and probe the zero tail without materializing the image.
/// </summary>
public class WbfsWriterTests
{
    private const int ClusterSize = 0x40000; // 256 KiB: the smallest size whose map fits u16

    [Fact]
    public void WiiIso_RoundTrips()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var wbfs = Convert(iso);
        Assert.Equal(iso, DecodePrefix(wbfs, iso.Length));
        AssertTailIsZero(wbfs);
    }

    [Fact]
    public void Header_HasExpectedFields()
    {
        var iso = TestWiiIsoBuilder.Build(Key(), 8, TestWiiIsoBuilder.RandomData(8));
        var wbfs = Convert(iso);

        Assert.Equal("WBFS"u8.ToArray(), wbfs.AsSpan(0, 4).ToArray());
        Assert.Equal((uint)(wbfs.Length / 512), ReadBe32(wbfs, 4));
        Assert.Equal(9, wbfs[8]); // hd_sector_shift: 512-byte sectors
        Assert.Equal(18, wbfs[9]); // wbfs_sector_shift: 256 KiB clusters
        Assert.Equal(1, wbfs[12]); // disc_table[0]
        Assert.Equal(0, wbfs.Length % 512);
    }

    [Fact]
    public void ZeroClusters_ShareOneVolumeCluster()
    {
        var iso = TestWiiIsoBuilder.Build(Key(), 8, TestWiiIsoBuilder.RandomData(8));
        var wbfs = Convert(iso);

        var blocksPerDisc = (int)((WbfsBlob.WiiDataSize + ClusterSize - 1) / ClusterSize);
        var contentClusters = (int)((iso.Length + ClusterSize - 1) / ClusterSize);
        var shared = ReadWlba(wbfs, contentClusters);
        for (var i = contentClusters; i < blocksPerDisc; i++)
        {
            Assert.Equal(shared, ReadWlba(wbfs, i));
        }

        // All-zero clusters are not stored: the file is far smaller than a fully allocated one.
        Assert.True(wbfs.Length < 768 + blocksPerDisc * 2L + (long)blocksPerDisc * ClusterSize,
            "zero clusters should not be stored");
    }

    [Fact]
    public void Scrub_ZeroesNonGamePartitionData()
    {
        var iso = TestWiiIsoBuilder.BuildWithUpdatePartition();
        var wbfs = Convert(iso, scrub: true);
        var decoded = DecodePrefix(wbfs, iso.Length);

        var dataStart = TestWiiIsoBuilder.UpdatePartitionOffset + TestWiiIsoBuilder.DataOffset;
        Assert.Equal(iso.AsSpan(0, dataStart).ToArray(), decoded.AsSpan(0, dataStart).ToArray());
        Assert.True(
            decoded.AsSpan(dataStart, TestWiiIsoBuilder.PartitionDataSize).IndexOfAnyExcept((byte)0) < 0,
            "the update partition data should be zeroed");
        var afterEnd = dataStart + TestWiiIsoBuilder.PartitionDataSize;
        Assert.Equal(iso.AsSpan(afterEnd).ToArray(), decoded.AsSpan(afterEnd).ToArray());
    }

    [Fact]
    public void GameCubeDisc_IsRejected()
    {
        var iso = new byte[0x8000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => WbfsWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void DiscWithoutMagic_IsRejected()
    {
        var iso = new byte[0x8000];
        new Random(42).NextBytes(iso);
        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => WbfsWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void ClusterSizeBelow32KiB_IsRejected()
    {
        var iso = TestWiiIsoBuilder.Build(Key(), 4, TestWiiIsoBuilder.RandomData(4));
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => WbfsWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new WbfsWriteOptions { BlockSize = 0x4000 }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void ClusterSizeTooSmallForMap_IsRejected()
    {
        // 32 KiB clusters need 286864 u16 map entries; the WBFS map holds 65535.
        var iso = TestWiiIsoBuilder.Build(Key(), 4, TestWiiIsoBuilder.RandomData(4));
        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => WbfsWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new WbfsWriteOptions { BlockSize = 0x8000 }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void NonPowerOfTwoClusterSize_IsRejected()
    {
        var iso = TestWiiIsoBuilder.Build(Key(), 4, TestWiiIsoBuilder.RandomData(4));
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => WbfsWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new WbfsWriteOptions { BlockSize = 0x30000 }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void NonSeekableOutput_IsRejected()
    {
        var iso = TestWiiIsoBuilder.Build(Key(), 4, TestWiiIsoBuilder.RandomData(4));
        using var output = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() => WbfsWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), output));
        Assert.Equal(0, output.Length);
    }

    private static byte[] Key()
    {
        return Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
    }

    private static byte[] Convert(byte[] iso, int clusterSize = ClusterSize, bool scrub = false)
    {
        using var ms = new MemoryStream();
        WbfsWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new WbfsWriteOptions
        {
            BlockSize = clusterSize,
            Scrub = scrub
        });
        return ms.ToArray();
    }

    private static byte[] DecodePrefix(byte[] wbfs, int length)
    {
        using var ms = new MemoryStream(wbfs);
        using var blob = WbfsBlob.Open(ms, leaveOpen: true);
        var output = new byte[length];
        var total = 0;
        while (total < output.Length)
        {
            var read = blob.ReadAt(total, output.AsSpan(total));
            Assert.True(read > 0, $"read stopped at 0x{total:X}");
            total += read;
        }

        return output;
    }

    /// <summary>Probes the end of the decoded image: shared zero clusters must decode to zeroes.</summary>
    private static void AssertTailIsZero(byte[] wbfs)
    {
        using var ms = new MemoryStream(wbfs);
        using var blob = WbfsBlob.Open(ms, leaveOpen: true);
        var probe = new byte[0x1000];
        Assert.Equal(probe.Length, blob.ReadAt(blob.Length - probe.Length, probe));
        Assert.True(probe.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    private static ushort ReadWlba(byte[] wbfs, int index)
    {
        var offset = 768 + index * 2;
        return (ushort)((wbfs[offset] << 8) | wbfs[offset + 1]);
    }

    private static uint ReadBe32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
