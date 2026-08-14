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
    public void WiiIso_ModifiedHashPadding_RoundTrips()
    {
        // Flip bytes inside the hash-area padding fields of a sector: the writer must emit
        // full 20-byte hash exceptions with Dolphin's overlapping windows (offsets +0 and
        // +12 for the 32-byte paddings, WIABlob.cpp:1452-1455). A partial trailing hash
        // would desync the fixed 22-byte exception entries.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130));
        var dataStart = TestWiiIsoBuilder.PartitionOffset + TestWiiIsoBuilder.DataOffset;
        foreach (var paddingOffset in new[] { 0x26C, 0x320, 0x3E0 }) // padding_0/1/2 of sector 5
        {
            var at = dataStart + 5 * 0x8000 + paddingOffset;
            iso[at] ^= 0xFF;
            iso[at + 0x10] ^= 0xFF;
        }

        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));
    }

    [Fact]
    public void WiiIso_OverlappingUpdatePartition_IsSkipped_AndDataRetained()
    {
        // A second, overlapping partition (an update partition) whose offset lies inside the
        // first partition's data. Dolphin skips it (WIABlob.cpp:967-971); the writer must not
        // encode it as a partition and must not lose any disc data.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130));

        // Replace the partition table with the real {count, table_offset<<2} layout: two
        // entries — the game partition and an update partition at 0x200000 (inside the first
        // partition's data, which spans [0x140000, 0x550000)).
        WriteBe32(iso, 0x40000, 2);
        WriteBe32(iso, 0x40004, 0x8000 >> 2);
        WriteBe32(iso, 0x8000, 0x100000 >> 2);
        WriteBe32(iso, 0x8004, 0);
        WriteBe32(iso, 0x8008, 0x200000 >> 2);
        WriteBe32(iso, 0x800C, 0);

        // Stamp the update partition's ticket + data header (its bytes live inside partition
        // 1's data region; they just need to look like a valid partition).
        WriteBe32(iso, 0x200000, 0x10001u);
        key.CopyTo(iso, 0x200000 + 0x1BF);
        WriteBe32(iso, 0x200000 + 0x2B8, 0x40000 >> 2);
        WriteBe32(iso, 0x200000 + 0x2BC, 0x100000 >> 2);

        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));

        using var ms = new MemoryStream(rvz);
        using var reader = RvzReader.Open(ms, leaveOpen: true);
        Assert.Single(reader.Partitions); // the update partition was not encoded separately
    }

    [Fact]
    public void WiiIso_OddSizedPartitionData_TailBecomesRaw()
    {
        // Partition data whose size is not a multiple of 0x8000: Dolphin encodes the whole
        // sectors and leaves the partial sector to be covered as raw data (WIABlob.cpp:
        // 921-933, 1039-1042) — the partition must not be bailed out entirely.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130));
        WriteBe32(iso, TestWiiIsoBuilder.PartitionOffset + 0x2BC, (uint)((130L * 0x8000 + 0x400) >> 2));

        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));

        using var ms = new MemoryStream(rvz);
        using var reader = RvzReader.Open(ms, leaveOpen: true);
        Assert.Single(reader.Partitions); // encoded as a partition, not raw
    }

    [Fact]
    public void DiscType_UnhashedWiiDisc_IsStillWii()
    {
        // disc_type comes from the volume, not from how the data is encoded (Dolphin:
        // WIABlob.cpp:1989-1996): a Wii disc without hashes/encryption is stored raw but is
        // still disc_type 2.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130));
        iso[0x60] = 1; // no hashes
        iso[0x61] = 1; // not encrypted

        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));
        using var ms = new MemoryStream(rvz);
        using var reader = RvzReader.Open(ms, leaveOpen: true);
        Assert.Equal(DiscType.Wii, reader.Disc.DiscType);
    }

    [Fact]
    public void DiscType_UnrecognizedDisc_IsUnknown()
    {
        // No GC or Wii magic: Dolphin writes disc_type 0, and the reader accepts it.
        var iso = new byte[0x200000];
        new Random(42).NextBytes(iso);

        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));
        using var ms = new MemoryStream(rvz);
        using var reader = RvzReader.Open(ms, leaveOpen: true);
        Assert.Equal(DiscType.Unknown, reader.Disc.DiscType);
    }

    [Fact]
    public void WiiIso_Scrubbed_ZeroesNonGamePartitionData()
    {
        // --scrub (Dolphin: ConvertCommand.cpp:170-197): the data of non-game partitions
        // (update/channel) is zeroed; the game partition and all raw areas stay intact.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130));

        // Add an update partition (type 0x10) AFTER the game partition's data
        // ([0x140000, 0x550000)): offset 0x600000, data at [0x640000, ...).
        WriteBe32(iso, 0x40000, 2);
        WriteBe32(iso, 0x40004, 0x8000 >> 2);
        WriteBe32(iso, 0x8000, 0x100000 >> 2);
        WriteBe32(iso, 0x8004, 0);          // game partition type
        WriteBe32(iso, 0x8008, 0x600000 >> 2);
        WriteBe32(iso, 0x800C, 0x10);       // update partition type
        WriteBe32(iso, 0x600000, 0x10001u);
        key.CopyTo(iso, 0x600000 + 0x1BF);
        WriteBe32(iso, 0x600000 + 0x2B8, 0x40000 >> 2);
        WriteBe32(iso, 0x600000 + 0x2BC, 0x100000 >> 2);

        var updateDataStart = 0x600000 + 0x40000; // clamped to the disc end by the scrubber
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        using var scrubbed = ScrubbedBlob.Create(blob);
        Assert.NotNull(scrubbed);
        var expected = new byte[iso.Length];
        iso.AsSpan(0, updateDataStart).CopyTo(expected);
        // [updateDataStart, iso.Length) is zeroed.
        scrubbed.ReadAt(0, expected);

        // The update partition's data reads as zeroes, the game partition's data is intact.
        var probe = new byte[iso.Length];
        scrubbed.ReadAt(0, probe);
        Assert.Equal(expected, probe);
        Assert.All(probe.AsSpan(updateDataStart).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(iso.AsSpan(0x140000, 0x410000).ToArray(), probe.AsSpan(0x140000, 0x410000).ToArray());

        // And the scrubbed image converts to RVZ and back byte-identically.
        using var ms = new MemoryStream();
        RvzWriter.Write(scrubbed, ms, new RvzWriteOptions
        {
            Compression = CompressionType.Zstd,
            Packing = true,
        });
        Assert.Equal(expected, Decode(ms.ToArray()));
    }

    [Fact]
    public void WiiIso_FirstRawEntry_StartsAt0x80()
    {
        // Dolphin's raw entries skip the first 0x80 bytes of the disc: they live in the
        // disc struct's disc_header (WIABlob.cpp:902-906), and the reader serves them from
        // the disc struct. Round-trip stays byte-identical.
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130));
        var rvz = Convert(iso, CompressionType.Zstd, packing: true);
        Assert.Equal(iso, Decode(rvz));

        // Check the stored raw table (the reader aligns entries down to 0x8000, so it
        // cannot be used for this assertion).
        var disc = WiaDisc.Parse(rvz.AsSpan(0x48, 0xDC));
        using var section = new MemoryStream(
            rvz.AsSpan((int)disc.RawDataEntriesOffset, (int)disc.RawDataEntriesSize).ToArray());
        using var decompressor = RVZSharp.Compression.CompressionCodecFactory.Create(disc.Compression)
            .CreateDecompressor(section, disc.ComprData.AsSpan(0, disc.ComprDataLen),
                disc.RawDataEntriesSize, disc.NumRawDataEntries * 0x18);
        var rawTable = new byte[disc.NumRawDataEntries * 0x18];
        var total = 0;
        while (total < rawTable.Length)
        {
            var read = decompressor.Read(rawTable, total, rawTable.Length - total);
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal(0x80ul, ReadBe64(rawTable, 0));
    }

    private static ulong ReadBe64(byte[] data, int offset) =>
        ((ulong)ReadBe32(data, offset) << 32) | ReadBe32(data, offset + 4);

    private static uint ReadBe32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

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
    public void ChunkSize_MultipleOf2MiB_RoundTrips()
    {
        // 6 MiB: a multiple of 2 MiB that is not a power of two — valid per Dolphin
        // (DiscUtils.cpp:210-236), previously rejected by the writer and the CLI.
        var iso = new byte[0x600000 * 3 + 0x1234];
        new Random(9).NextBytes(iso);
        iso[0x1C] = 0xC2; // GC DVD magic (0xC2339F3D)
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        using var ms = new MemoryStream();
        RvzWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new RvzWriteOptions
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x600000,
        });
        Assert.Equal(iso, Decode(ms.ToArray()));
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

        // Regression: the raw table's number_of_groups field must be written (not zero), so
        // Dolphin's reader (which loops i < number_of_groups) serves the raw areas.
        using var ms = new MemoryStream(rvz);
        using var reader = RvzReader.Open(ms, leaveOpen: true);
        Assert.NotEmpty(reader.RawDataEntries);
        Assert.All(reader.RawDataEntries, entry => Assert.True(entry.NumGroups > 0));
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
