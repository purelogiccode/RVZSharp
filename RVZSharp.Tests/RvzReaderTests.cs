using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class RvzReaderTests
{
    private static RvzSpec GcSpec(CompressionType compression, uint chunkSize, HashSet<int>? packed = null)
    {
        return new RvzSpec
        {
            Compression = compression,
            ChunkSize = chunkSize,
            DiscType = DiscType.GameCube,
            RawSize = (int)(5 * chunkSize) + 0x12345,
            PackedChunks = packed ?? [],
            Seed = 7
        };
    }

    private static RvzSpec WiiSpec(CompressionType compression, uint chunkSize, bool exceptions,
        HashSet<int>? packed = null)
    {
        return new RvzSpec
        {
            Compression = compression,
            ChunkSize = chunkSize,
            DiscType = DiscType.Wii,
            RawSize = 0x18000,
            RawTailSize = 0x28000,
            Partition = new PartitionSpec
            {
                SectorCount = 70,
                Exceptions = exceptions ? MakeExceptions() : []
            },
            PackedChunks = packed ?? [],
            Seed = 3
        };
    }

    private static HashExceptionEntry[][] MakeExceptions()
    {
        var e0 = new[]
        {
            new HashExceptionEntry(0x100, [.. Enumerable.Range(0, 20).Select(i => (byte)i)]),
            new HashExceptionEntry(0x3E0, [.. Enumerable.Range(0, 20).Select(i => (byte)(0x80 + i))])
        };
        var e1 = new[]
        {
            new HashExceptionEntry(0x200, [.. Enumerable.Range(0, 20).Select(i => (byte)(0x40 + i))])
        };
        return [e0, e1];
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Lzma2)]
    public void GameCube_FullDecode_EveryCodec(CompressionType compression)
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(GcSpec(compression, 0x200000));
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        Assert.Equal(iso.Length, reader.Length);
        Assert.Equal(iso, reader.ReadFully());
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Lzma2)]
    public void GameCube_Packing(CompressionType compression)
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(
            GcSpec(compression, 0x200000, packed: [0, 2, 4]));
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        Assert.Equal(iso, reader.ReadFully());
    }

    [Fact]
    public void GameCube_SmallChunks()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(GcSpec(CompressionType.Zstd, 0x8000));
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        Assert.Equal(iso, reader.ReadFully());
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    public void Wii_FullDecode(CompressionType compression)
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(WiiSpec(compression, 0x200000, exceptions: true));
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        Assert.Equal(iso, reader.ReadFully());
    }

    [Fact]
    public void Wii_WithPackingAndExceptions()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(
            WiiSpec(CompressionType.Zstd, 0x200000, exceptions: true, packed: [1, 4]));
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        Assert.Equal(iso, reader.ReadFully());
    }

    [Fact]
    public void Wii_SmallChunks()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(
            WiiSpec(CompressionType.Lzma2, 0x8000, exceptions: true));
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        Assert.Equal(iso, reader.ReadFully());
    }

    [Fact]
    public void RandomAccess_MatchesFullDecode()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(
            WiiSpec(CompressionType.Zstd, 0x200000, exceptions: true, packed: [2, 5]));
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        var offsets = new long[] { 0, 0x40, 0x80, 0x12345, 0x18000, 0x300000, 0x3FFFFF, 0x400000 };
        foreach (var offset in offsets)
        {
            if (offset >= iso.Length)
            {
                continue;
            }

            var count = (int)Math.Min(0x12345, iso.Length - offset);
            var expected = iso.AsSpan((int)offset, count).ToArray();
            var actual = new byte[count];
            var read = reader.ReadAt(offset, actual);
            Assert.Equal(count, read);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Open_BadMagic_Throws()
    {
        var bytes = TestRvzBuilder.Build(GcSpec(CompressionType.None, 0x200000));
        bytes[0] = (byte)'X';
        Assert.Throws<RvzFormatException>(() => RvzReader.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void Open_TruncatedFile_Throws()
    {
        var bytes = TestRvzBuilder.Build(GcSpec(CompressionType.None, 0x200000));
        Assert.Throws<RvzFormatException>(() =>
            RvzReader.Open(new MemoryStream([.. bytes.AsSpan(0, bytes.Length / 2)])));
    }

    [Fact]
    public void Open_TruncatedTableSection_ThrowsFormatException()
    {
        // A table section extending past EOF must fail with RvzFormatException, not a raw
        // ArgumentOutOfRangeException (Dolphin: OffsetRead fails cleanly, WIABlob.cpp:168-171).
        var bytes = TestRvzBuilder.Build(GcSpec(CompressionType.Zstd, 0x200000));
        var disc = WiaDisc.Parse(bytes.AsSpan(0x48, 0xDC));
        var cut = (int)disc.GroupEntriesOffset + 10;

        Assert.Throws<RvzFormatException>(() =>
            RvzReader.Open(new MemoryStream([.. bytes.AsSpan(0, cut)])));
    }

    [Fact]
    public void Open_InvalidHashExceptionOffset_Throws()
    {
        // A hash exception whose 20-byte window overruns the 0x400 hash area
        // (offset_in_block + 20 > 0x400) must fail the read, not be silently dropped
        // (Dolphin: WIABlob.cpp:868-876).
        var rvz = TestRvzBuilder.Build(WiiSpec(CompressionType.None, 0x200000, exceptions: true));
        var disc = WiaDisc.Parse(rvz.AsSpan(0x48, 0xDC));
        // Segment 0's group_index lives at partition-entry offset 0x18; the group table is
        // uncompressed (None) with 12-byte RVZ entries.
        var firstPartitionGroup = ReadBe32(rvz,
            (int)disc.GroupEntriesOffset + (int)ReadBe32(rvz, (int)disc.PartitionEntriesOffset + 0x18) * 12);
        var groupOffset = (int)(firstPartitionGroup * 4L); // data_off4 << 2

        // The exception list sits at the start of the group (None compression): count(2)
        // then 22-byte entries. Patch the first entry's offset from 0x0100 to 0x03F0,
        // whose 20-byte window overruns the 0x400 hash area.
        rvz[groupOffset + 2] = 0x03;
        rvz[groupOffset + 3] = 0xF0;

        using var reader = RvzReader.Open(new MemoryStream(rvz));
        Assert.Throws<RvzFormatException>(() => reader.ReadFully());
    }

    [Fact]
    public void Open_TamperedGroupTableHash_Throws()
    {
        // The disc hash covers the disc struct: tamper the stored part hash → disc hash mismatch
        // is detected at Open (the part hash itself lives in the disc struct).
        var bytes = TestRvzBuilder.Build(GcSpec(CompressionType.None, 0x200000));
        bytes[0x48 + 0x10] ^= 0xFF; // inside dhead → disc hash mismatch
        Assert.Throws<RvzHashMismatchException>(() => RvzReader.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void Open_OverlappingRawData_Throws()
    {
        // Dolphin's HasDataOverlap (WIABlob.cpp:244-277): a raw entry covering the
        // partition's data must be rejected.
        var bytes = TestRvzBuilder.Build(WiiSpec(CompressionType.None, 0x200000, exceptions: false));
        var disc = WiaDisc.Parse(bytes.AsSpan(0x48, 0xDC));
        var partitionStart = ReadBe32(bytes, (int)disc.PartitionEntriesOffset + 0x10) * 0x8000L;

        // Point the second raw entry (the tail) at the partition's data start.
        WriteBe64(bytes, (int)disc.RawDataEntriesOffset + 0x18, (ulong)partitionStart);

        Assert.Throws<RvzFormatException>(() => RvzReader.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void Open_OutOfOrderPartitionSegments_Throws()
    {
        // Dolphin rejects partitions whose segment 1 starts before segment 0
        // (WIABlob.cpp:204-208).
        var bytes = TestRvzBuilder.Build(WiiSpec(CompressionType.None, 0x200000, exceptions: false));
        var disc = WiaDisc.Parse(bytes.AsSpan(0x48, 0xDC));
        var partTable = (int)disc.PartitionEntriesOffset;

        // Segment 1 currently empty (first_sector 0); make it non-empty and out of order:
        // first_sector(1) = first_sector(0) - 1.
        var firstSector0 = ReadBe32(bytes, partTable + 0x10);
        WriteBe32(bytes, partTable + 0x20, firstSector0 - 1);
        WriteBe32(bytes, partTable + 0x24, 1); // num_sectors
        RehashContainer(bytes);

        Assert.Throws<RvzFormatException>(() => RvzReader.Open(new MemoryStream(bytes)));
    }

    /// <summary>
    /// Recomputes the partition-table hash, the disc-struct hash and the file-head hash
    /// after patching the partition table, so the container still parses.
    /// </summary>
    private static void RehashContainer(byte[] rvz)
    {
        var disc = WiaDisc.Parse(rvz.AsSpan(0x48, 0xDC));
        var partSize = (int)(ReadBe32(rvz, 0x48 + 0x90) * ReadBe32(rvz, 0x48 + 0x94));
        System.Security.Cryptography.SHA1.HashData(rvz.AsSpan((int)disc.PartitionEntriesOffset, partSize))
            .CopyTo(rvz, 0x48 + 0xA0);
        System.Security.Cryptography.SHA1.HashData(rvz.AsSpan(0x48, 0xDC)).CopyTo(rvz, 0x10);
        System.Security.Cryptography.SHA1.HashData(rvz.AsSpan(0, 0x34)).CopyTo(rvz, 0x34);
    }

    private static uint ReadBe32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteBe64(byte[] data, int offset, ulong value)
    {
        WriteBe32(data, offset, (uint)(value >> 32));
        WriteBe32(data, offset + 4, (uint)value);
    }
}

public class RvzReaderMatrixTests
{
    [Theory]
    [InlineData(CompressionType.Bzip2, 0x80000)]
    [InlineData(CompressionType.Lzma, 0x80000)]
    [InlineData(CompressionType.Lzma2, 0x10000)]
    [InlineData(CompressionType.Zstd, 0x400000)] // 4 MiB chunks: 2 exception lists per partition chunk
    [InlineData(CompressionType.None, 0x200000)]
    public void Wii_MoreCombinations(CompressionType compression, uint chunkSize)
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = compression,
            ChunkSize = chunkSize,
            DiscType = DiscType.Wii,
            RawSize = 0x28000,
            RawTailSize = 0x18000,
            Partition = new PartitionSpec
            {
                SectorCount = 130, // spans 3 regions
                Exceptions =
                [
                    [new HashExceptionEntry(0x100, new byte[20])],
                    [new HashExceptionEntry(0x500, new byte[20])],
                    []
                ]
            },
            PackedChunks = [0, 2, 5],
            Seed = 11
        });

        using var reader = RvzReader.Open(new MemoryStream(rvz));
        Assert.Equal(iso, reader.ReadFully());
    }

    [Fact]
    public void CorruptGroupData_Throws()
    {
        var (rvz, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x200000,
            RawSize = 0x18000,
            Seed = 2
        });

        // Corrupt a byte inside the first group's stored data.
        var disc = WiaDisc.Parse(rvz.AsSpan(0x48, 0xDC));
        var gs = rvz.AsSpan((int)disc.GroupEntriesOffset, (int)disc.GroupEntriesSize).ToArray();
        byte[] table;
        using (var ms = new MemoryStream(gs))
        using (var d = Compression.CompressionCodecFactory.Create(disc.Compression)
                   .CreateDecompressor(ms, disc.ComprData.AsSpan(0, disc.ComprDataLen), gs.Length, disc.NumGroups * 12))
        {
            table = new byte[disc.NumGroups * 12];
            var t = 0;
            while (t < table.Length)
            {
                var n = d.Read(table, t, table.Length - t);
                if (n <= 0)
                {
                    break;
                }

                t += n;
            }
        }

        var g0 = RvzGroupEntry.Parse(table.AsSpan(0, 12));
        var corrupted = (byte[])rvz.Clone();
        corrupted[(int)g0.FileOffset] ^= 0xFF; // break the zstd frame magic

        using var reader = RvzReader.Open(new MemoryStream(corrupted));
        Assert.ThrowsAny<RvzException>(() => reader.ReadFully());
    }
}
