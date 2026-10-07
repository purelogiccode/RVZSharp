using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// End-to-end <see cref="WiaWriter"/> tests: convert a synthetic disc image (GameCube or Wii,
/// with random data, zero regions and hash exceptions) to WIA and decode it back byte-exactly
/// through <see cref="RvzReader.OpenWia"/>.
/// </summary>
public class WiaWriterTests
{
    public static TheoryData<CompressionType> CompressionCases => new()
    {
        CompressionType.None,
        CompressionType.Purge,
        CompressionType.Bzip2,
        CompressionType.Lzma,
        CompressionType.Lzma2
    };

    [Theory]
    [MemberData(nameof(CompressionCases))]
    public void GameCubeIso_RoundTrips(CompressionType compression)
    {
        var iso = BuildGcIso();
        var wia = Convert(iso, compression);
        Assert.Equal(iso, Decode(wia));
    }

    [Theory]
    [MemberData(nameof(CompressionCases))]
    public void WiiIso_RoundTrips(CompressionType compression)
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var wia = Convert(iso, compression);
        Assert.Equal(iso, Decode(wia));
    }

    [Theory]
    [MemberData(nameof(CompressionCases))]
    public void WiiIso_4MiBChunks_RoundTrips(CompressionType compression)
    {
        // Chunks larger than 2 MiB span whole hash regions and carry one exception list per
        // region (Dolphin: exception_lists_per_chunk = max(1, chunk_size / 2 MiB)); the final
        // partial chunk still carries the full list count.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var wia = Convert(iso, compression, chunkSize: 0x400000);
        Assert.Equal(iso, Decode(wia));
    }

    [Fact]
    public void WiiIso_6MiBChunk_RoundTrips()
    {
        // A non-power-of-two multi-region chunk: 6 MiB spans three 2 MiB regions, so the
        // chunk must carry three exception lists (the final partial chunk still carries all
        // three, two of them empty).
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var wia = Convert(iso, CompressionType.Lzma2, chunkSize: 0x600000);
        Assert.Equal(iso, Decode(wia));
    }

    [Fact]
    public void DefaultOptions_UseLzma2_AndRoundTrip()
    {
        var iso = BuildGcIso();
        using var ms = new MemoryStream();
        WiaWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms);
        var wia = ms.ToArray();

        var disc = WiaDisc.Parse(wia.AsSpan(WiaFileHead.Size, WiaDisc.Size));
        Assert.Equal(CompressionType.Lzma2, disc.Compression);
        Assert.Equal(iso, Decode(wia));
    }

    [Fact]
    public void Output_UsesWiaMagicAndVersion()
    {
        var iso = BuildGcIso();
        var wia = Convert(iso, CompressionType.Lzma2);

        Assert.Equal("WIA\x01"u8.ToArray(), wia[..4]);
        var head = WiaFileHead.Parse(wia.AsSpan(0, WiaFileHead.Size));
        Assert.True(head.IsWia);
        Assert.Equal(WiaFileHead.ImplementedVersion, head.Version);
        Assert.Equal(WiaFileHead.WiaVersionWriteCompatible, head.VersionCompatible);
    }

    [Fact]
    public void Packing_IsIgnored()
    {
        // WIA has no packing stage: the option must not change the output.
        var iso = BuildGcIso();
        var packed = Convert(iso, CompressionType.Lzma2, packing: true);
        var unpacked = Convert(iso, CompressionType.Lzma2, packing: false);
        Assert.Equal(packed, unpacked);
    }

    [Fact]
    public void Zstd_IsRejected()
    {
        var iso = BuildGcIso();
        using var ms = new MemoryStream();
        Assert.Throws<RvzUnsupportedException>(() => WiaWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
            {
                Compression = CompressionType.Zstd
            }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void NonMultipleOf2MiBChunkSize_IsRejected()
    {
        var iso = BuildGcIso();
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => WiaWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
            {
                Compression = CompressionType.None,
                ChunkSize = 0x10000
            }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void DiscWithoutMagic_IsRejected()
    {
        var iso = new byte[0x200000];
        new Random(42).NextBytes(iso);

        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => WiaWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
            {
                Compression = CompressionType.None
            }));
        Assert.Equal(0, ms.Length);
    }

    private static byte[] Convert(byte[] iso, CompressionType compression, bool packing = true,
        int chunkSize = (int)WiaDisc.GroupSize)
    {
        using var ms = new MemoryStream();
        WiaWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
        {
            Compression = compression,
            ChunkSize = chunkSize,
            Packing = packing
        });
        return ms.ToArray();
    }

    private static byte[] Decode(byte[] wia)
    {
        using var ms = new MemoryStream(wia);
        using var reader = RvzReader.OpenWia(ms, leaveOpen: true);
        var iso = new byte[reader.Length];
        reader.ReadAt(0, iso);
        return iso;
    }

    /// <summary>A GameCube-style ISO: random data with a zero region and the GC disc magic.</summary>
    private static byte[] BuildGcIso()
    {
        var iso = new byte[0x200000 * 2 + 0x20000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2; // GC DVD magic (0xC2339F3D) so the writer treats it as a GC disc
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        Array.Clear(iso, 0x100000, 0x40000); // zero region
        return iso;
    }
}
