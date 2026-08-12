using RVZSharp.Blobs;
using RVZSharp.Format;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// End-to-end RvzWriter tests: convert a synthetic disc image (GameCube or Wii, with random
/// data, zero regions, LFG junk, and hash exceptions) to RVZ and decode it back to the exact
/// same bytes.
/// </summary>
public class RvzWriterTests
{
    public static TheoryData<CompressionType, bool> CompressionCases => new()
    {
        { CompressionType.None, false },
        { CompressionType.None, true },
        { CompressionType.Zstd, false },
        { CompressionType.Zstd, true },
        { CompressionType.Bzip2, true },
        { CompressionType.Lzma, true },
        { CompressionType.Lzma2, true },
    };

    [Theory]
    [MemberData(nameof(CompressionCases))]
    public void GameCubeIso_RoundTrips(CompressionType compression, bool packing)
    {
        var iso = BuildGcIso();
        var rvz = Convert(iso, compression, packing);
        Assert.Equal(iso, Decode(rvz));
    }

    [Theory]
    [MemberData(nameof(CompressionCases))]
    public void WiiIso_RoundTrips(CompressionType compression, bool packing)
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var rvz = Convert(iso, compression, packing);
        Assert.Equal(iso, Decode(rvz));
    }

    [Theory]
    [MemberData(nameof(CompressionCases))]
    public void WiiIso_WithFstSplit_RoundTrips(CompressionType compression, bool packing)
    {
        // A partition with an FST area, so the writer splits it into two data entries.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130));
        // Point the FST at 0x40: size 0x40 (shifted) → split point after 2 MiB of data.
        WriteBe32(iso, TestWiiIsoBuilder.PartitionOffset + 0x424, 0x40 >> 2);
        WriteBe32(iso, TestWiiIsoBuilder.PartitionOffset + 0x428, 0x40 >> 2);
        var rvz = Convert(iso, compression, packing);
        Assert.Equal(iso, Decode(rvz));
    }

    [Fact]
    public void AllZeroIso_ProducesTinyFile()
    {
        var iso = new byte[0x200000 * 3];
        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        // The zero chunks become zero groups; only the tables/headers remain.
        Assert.True(rvz.Length < 0x1000, $"expected a tiny RVZ, got {rvz.Length} bytes");
        Assert.Equal(iso, Decode(rvz));
    }

    [Fact]
    public void JunkOnlyIso_PacksWithRecoveredSeed()
    {
        var seed = new byte[68];
        new Random(3).NextBytes(seed);
        var iso = new byte[0x200000];
        // One junk region (matching the format's skip semantics) at an unaligned offset.
        var junk = ReferencePrng.Generate(seed, 0x1234, iso.Length - 0x1234);
        junk.CopyTo(iso, 0x1234);

        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));

        // With packing, the junk should be stored as a 68-byte seed + size header.
        using var ms = new MemoryStream(rvz);
        using var reader = RvzReader.Open(ms, leaveOpen: true);
        Assert.True(reader.GroupEntries.Length == 1);
        Assert.True(reader.GroupEntries[0].RvzPackedSize > 0);
    }

    [Fact]
    public void ChunkSize_SmallerThan2MiB_RoundTrips()
    {
        var iso = BuildGcIso();
        using var ms = new MemoryStream();
        RvzWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x8000,
        });
        Assert.Equal(iso, Decode(ms.ToArray()));
    }

    [Fact]
    public void WiiIso_SmallChunks_WithHashExceptions_RoundTrips()
    {
        // Small chunks split the 2 MiB regions into several groups; the hash exceptions
        // must be converted to chunk-relative offsets.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        using var ms = new MemoryStream();
        RvzWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x10000,
        });
        Assert.Equal(iso, Decode(ms.ToArray()));
    }

    [Fact]
    public void InvalidChunkSize_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => RvzWriter.Write(
            PlainBlob.Open(new MemoryStream(new byte[0x10000])), ms, new RvzWriteOptions
            {
                ChunkSize = 0x30000, // not a power of two
            }));
    }

    [Fact]
    public void LegacyFormats_ConvertToRvz_AndDecodeBack()
    {
        // GCZ, TGC, NFS, WIA and a small-block CISO all convert to RVZ and decode to the
        // original image bytes. (WBFS is omitted: its fixed 9.4 GiB size would make the test
        // read gigabytes of zero clusters.)
        var iso = BuildGcIso();

        var gcz = TestLegacyBuilders.BuildGcz(iso);
        Assert.Equal(iso, RoundTripViaRvz(gcz));

        var (tgc, tgcIso) = TestLegacyBuilders.BuildTgc();
        Assert.Equal(tgcIso, RoundTripViaRvz(tgc, tgcIso.Length));

        // WIA (from the RVZ builder with a WIA mode) converts to RVZ and back.
        var spec = new RvzSpec
        {
            IsWia = true,
            Compression = CompressionType.None,
            ChunkSize = WiaDisc.GroupSize,
            RawSize = 3 * 0x8000 + 0x1234,
            RawTailSize = 0x9000,
            Seed = 4,
        };
        var wia = TestRvzBuilder.Build(spec);
        var wiaIso = TestRvzBuilder.BuildWithIso(spec).Iso;
        Assert.Equal(wiaIso, RoundTripViaRvz(wia, wiaIso.Length));

        // CISO with a small block size keeps the image small.
        var ciso = TestLegacyBuilders.BuildCiso(iso, blockSize: 0x100, presentBlocks: Enumerable.Range(0, 0x4200));
        Assert.Equal(iso, RoundTripViaRvz(ciso, iso.Length));
    }

    [Fact]
    public void Nfs_ConvertsToRvz_AndDecodeBack()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var (nfs, nfsIso) = TestLegacyBuilders.BuildNfs(key, blockCount: 5, ranges: [(0u, 5u)]);

        // NFS only opens when its directory is named "content" (Dolphin's requirement).
        var dir = Path.Combine(Path.GetTempPath(), "rvzsharp-test-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        Directory.CreateDirectory(Path.Combine(dir, "code"));
        var path = Path.Combine(dir, "content", "hif_000000.nfs");
        File.WriteAllBytes(path, nfs);
        File.WriteAllBytes(Path.Combine(dir, "code", "htk.bin"), key);
        try
        {
            using var file = File.OpenRead(path);
            using var blob = Blob.Open(file, filePath: path, leaveOpen: true);
            using var outStream = new MemoryStream();
            RvzWriter.Write(blob, outStream, RvzWriteOptions.Default);
            var rvz = outStream.ToArray();

            using var rvzStream = new MemoryStream(rvz);
            using var reader = RvzReader.Open(rvzStream, leaveOpen: true);
            var decoded = new byte[blob.Length];
            reader.ReadAt(0, decoded);
            Assert.Equal(nfsIso, decoded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GameCubeIso_SizeWithSmallChunkRemainder_RoundTrips()
    {
        // The first raw area is read from sector-aligned offset 0 (growing the read size by
        // 0x80); the raw table's group count must cover that (Dolphin reads by n_groups).
        var iso = BuildGcIso().Take(0x400000 + 0x40).ToArray(); // % chunkSize ∈ (0, 0x80]
        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));
    }

    [Fact]
    public void WiiIso_JunkInPartitionData_RoundTrips()
    {
        // The partition payload contains real LFG junk, so the writer's packing stage runs
        // on partition data as well as raw data.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var data = TestWiiIsoBuilder.RandomData(130);
        var seed = new byte[68];
        new Random(11).NextBytes(seed);
        var junk = ReferencePrng.Generate(seed, 0x7C00 * 3 + 0x123, 0x100000);
        junk.CopyTo(data, 0x7C00 * 3 + 0x123);
        var iso = TestWiiIsoBuilder.Build(key, 130, data);
        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));
    }

    private static byte[] RoundTripViaRvz(byte[] legacyFile, int? bytes = null)
    {
        using var file = new MemoryStream(legacyFile);
        using var blob = Blob.Open(file, filePath: "test", leaveOpen: true);
        using var outStream = new MemoryStream();
        RvzWriter.Write(blob, outStream, RvzWriteOptions.Default);
        var rvz = outStream.ToArray();

        using var rvzStream = new MemoryStream(rvz);
        using var reader = RvzReader.Open(rvzStream, leaveOpen: true);
        var decoded = new byte[bytes ?? blob.Length];
        reader.ReadAt(0, decoded);
        return decoded;
    }

    private static byte[] Convert(byte[] iso, CompressionType compression, bool packing)
    {
        using var ms = new MemoryStream();
        RvzWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
        {
            Compression = compression,
            Packing = packing,
        });
        return ms.ToArray();
    }

    private static byte[] Decode(byte[] rvz)
    {
        using var ms = new MemoryStream(rvz);
        using var reader = RvzReader.Open(ms, leaveOpen: true);
        var iso = new byte[reader.Length];
        reader.ReadAt(0, iso);
        return iso;
    }

    /// <summary>
    /// A GameCube-style ISO: random data with zero regions and LFG junk regions at aligned
    /// and unaligned offsets (junk is generated with the format's offset % 0x8000 skip).
    /// </summary>
    private static byte[] BuildGcIso()
    {
        var iso = new byte[0x200000 * 2 + 0x20000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2; // GC DVD magic (0xC2339F3D) so the writer treats it as a GC disc
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        Array.Clear(iso, 0x100000, 0x40000); // zero region

        var seed = new byte[68];
        new Random(5).NextBytes(seed);
        var junk1 = ReferencePrng.Generate(seed, 0x200000, 0x80000); // aligned junk region
        junk1.CopyTo(iso, 0x200000);
        var junk2 = ReferencePrng.Generate(seed, 0x220000 + 0x1234, 0x30000); // unaligned
        junk2.CopyTo(iso, 0x220000 + 0x1234);
        return iso;
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
