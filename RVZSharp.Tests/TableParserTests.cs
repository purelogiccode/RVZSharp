using System.Security.Cryptography;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class TableParserTests
{
    private static (MemoryStream File, byte[] DiscBytes) BuildFile(
        TestDiscBuilder disc, byte[] partTable, byte[] rawTable, byte[] groupTable)
    {
        disc.PartitionEntriesOffset = 0x48 + WiaDisc.Size;
        disc.RawDataEntriesOffset = disc.PartitionEntriesOffset + (ulong)partTable.Length;
        disc.GroupEntriesOffset = disc.RawDataEntriesOffset + (ulong)rawTable.Length;
        disc.RawDataEntriesSize = (uint)rawTable.Length;
        disc.GroupEntriesSize = (uint)groupTable.Length;
        var discBytes = disc.Build();

        var ms = new MemoryStream();
        ms.Write(new byte[0x48]);
        ms.Write(discBytes);
        ms.Write(partTable);
        ms.Write(rawTable);
        ms.Write(groupTable);
        ms.Position = 0;
        return (ms, discBytes);
    }

    private static byte[] WriteBeU32(uint value)
    {
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static byte[] WriteBeU64(ulong value)
    {
        var b = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            b[i] = (byte)(value >> (56 - 8 * i));
        }

        return b;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        var offset = 0;
        foreach (var a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }

        return result;
    }

    private static byte[] MakePartEntry(uint firstSector, uint numSectors, uint groupIndex, uint numGroups, byte keySeed = 0)
    {
        var b = new byte[0x30];
        for (var i = 0; i < 16; i++)
        {
            b[i] = (byte)(keySeed + i);
        }

        var pd = Concat(WriteBeU32(firstSector), WriteBeU32(numSectors), WriteBeU32(groupIndex), WriteBeU32(numGroups));
        pd.CopyTo(b, 16);
        return b;
    }

    private static byte[] MakeRawEntry(ulong offset, ulong size, uint groupIndex, uint numGroups)
    {
        return Concat(WriteBeU64(offset), WriteBeU64(size), WriteBeU32(groupIndex), WriteBeU32(numGroups));
    }

    private static byte[] MakeGroupEntry(uint dataOff4, uint dataSize, uint packedSize)
    {
        return Concat(WriteBeU32(dataOff4), WriteBeU32(dataSize), WriteBeU32(packedSize));
    }

    /// <summary>Configures a disc builder for the given compression method (props included).</summary>
    private static void ConfigureCompression(TestDiscBuilder disc, CompressionType compression)
    {
        disc.Compression = compression;
        switch (compression)
        {
            case CompressionType.None:
            case CompressionType.Bzip2:
            case CompressionType.Zstd:
                disc.ComprDataLen = 0;
                break;
            case CompressionType.Lzma:
                {
                    var (props, _) = TestCompressor.EncodeLzma1([1], endMarker: true);
                    props.CopyTo(disc.ComprData, 0);
                    disc.ComprDataLen = (byte)props.Length;
                    break;
                }
            case CompressionType.Lzma2:
                disc.ComprData[0] = 21;
                disc.ComprDataLen = 1;
                break;
        }
    }

    [Fact]
    public void ParsePartitions_ValidTable_ReturnsEntries()
    {
        var partTable = Concat(
            MakePartEntry(0x1000, 0x20, 0, 2, keySeed: 1),
            MakePartEntry(0x2000, 0x40, 2, 4, keySeed: 9));
        var disc = new TestDiscBuilder
        {
            DiscType = DiscType.Wii,
            NumPartitions = 2,
            PartitionEntriesHash = SHA1.HashData(partTable)
        };
        var (file, discBytes) = BuildFile(disc, partTable, [], []);
        using (file)
        {
            var entries = TableParser.ParsePartitions(file, WiaDisc.Parse(discBytes));

            Assert.Equal(2, entries.Length);
            Assert.Equal(0x1000u, entries[0].Data[0].FirstSector);
            Assert.Equal(0x20u, entries[0].Data[0].NumSectors);
            Assert.Equal(0u, entries[0].Data[0].GroupIndex);
            Assert.Equal(2u, entries[0].Data[0].NumGroups);
            Assert.Equal(1, entries[0].Key[0]);
            Assert.Equal(9, entries[1].Key[0]);
        }
    }

    [Fact]
    public void ParsePartitions_NoPartitions_ReturnsEmpty()
    {
        // Dolphin verifies the partition-table hash even when the table is empty
        // (WIABlob.cpp:165-175): it must be SHA-1 of the empty byte buffer.
        var disc = new TestDiscBuilder
        {
            NumPartitions = 0,
            PartitionEntriesHash = SHA1.HashData(ReadOnlySpan<byte>.Empty)
        };
        var (file, discBytes) = BuildFile(disc, [], [], []);
        using (file)
        {
            Assert.Empty(TableParser.ParsePartitions(file, WiaDisc.Parse(discBytes)));
        }
    }

    [Fact]
    public void ParsePartitions_NoPartitions_BadHash_Throws()
    {
        // An empty partition table with a non-matching hash must be rejected (the default
        // TestDiscBuilder hash is all zeroes).
        var disc = new TestDiscBuilder { NumPartitions = 0 };
        var (file, discBytes) = BuildFile(disc, [], [], []);
        using (file)
        {
            Assert.Throws<RvzHashMismatchException>(() =>
                TableParser.ParsePartitions(file, WiaDisc.Parse(discBytes)));
        }
    }

    [Fact]
    public void ParsePartitions_HashMismatch_Throws()
    {
        var partTable = MakePartEntry(0x1000, 0x20, 0, 2);
        var disc = new TestDiscBuilder
        {
            DiscType = DiscType.Wii,
            NumPartitions = 1,
            PartitionEntriesHash = new byte[WiaDisc.HashSize]
        };
        var (file, discBytes) = BuildFile(disc, partTable, [], []);
        using (file)
        {
            Assert.Throws<RvzHashMismatchException>(() =>
                TableParser.ParsePartitions(file, WiaDisc.Parse(discBytes)));
        }
    }

    [Fact]
    public void ParsePartitions_EntrySizeSmallerThan030_ZeroFills()
    {
        // Dolphin accepts entries smaller than 0x30 and zero-fills the remainder
        // (WIABlob.cpp:177-185: copy_length = min(entry_size, sizeof(PartitionEntry))).
        var partTable = new byte[0x20];
        // 16-byte key at 0..15, first data entry at 16 (the second entry is beyond the
        // declared entry size and must be zero-filled).
        WriteBeU32(0x1234).CopyTo(partTable, 0x10); // first_sector
        WriteBeU32(0x20).CopyTo(partTable, 0x14); // number_of_sectors
        var disc = new TestDiscBuilder
        {
            DiscType = DiscType.Wii,
            NumPartitions = 1,
            PartitionEntrySize = 0x20,
            PartitionEntriesHash = SHA1.HashData(partTable)
        };
        var (file, discBytes) = BuildFile(disc, partTable, [], []);
        using (file)
        {
            var entries = TableParser.ParsePartitions(file, WiaDisc.Parse(discBytes));
            Assert.Single(entries);
            Assert.Equal(0x1234u, entries[0].Data[0].FirstSector);
            Assert.Equal(0x20u, entries[0].Data[0].NumSectors);
            Assert.Equal(0u, entries[0].Data[1].FirstSector); // zero-filled tail
            Assert.Equal(0u, entries[0].Data[1].NumSectors);
        }
    }

    [Fact]
    public void ParsePartitions_LargerEntrySize_ExtraBytesIgnored()
    {
        var partTable = Concat(MakePartEntry(0x1000, 0x20, 0, 2), new byte[8]); // 0x38 per entry
        var disc = new TestDiscBuilder
        {
            DiscType = DiscType.Wii,
            NumPartitions = 1,
            PartitionEntrySize = 0x38,
            PartitionEntriesHash = SHA1.HashData(partTable)
        };
        var (file, discBytes) = BuildFile(disc, partTable, [], []);
        using (file)
        {
            var entries = TableParser.ParsePartitions(file, WiaDisc.Parse(discBytes));

            Assert.Single(entries);
            Assert.Equal(0x1000u, entries[0].Data[0].FirstSector);
        }
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Lzma2)]
    public void ParseRawDataEntries_EveryCodec(CompressionType compression)
    {
        // First raw entry mimics the real first entry: offset 0x80, size 0x4FF80.
        var rawTable = Concat(
            MakeRawEntry(0x80, 0x4FF80, 0, 1),
            MakeRawEntry(0x50000, 0x100000, 1, 1));
        var stored = TestCompressor.Compress(compression, rawTable);
        var disc = new TestDiscBuilder { NumRawDataEntries = 2 };
        ConfigureCompression(disc, compression);
        var (file, discBytes) = BuildFile(disc, [], stored, []);
        using (file)
        {
            var entries = TableParser.ParseRawDataEntries(file, WiaDisc.Parse(discBytes));

            Assert.Equal(2, entries.Length);
            // Alignment fixup: 0x80 → 0, size grows by 0x80 so the end stays at 0x50000.
            Assert.Equal(0ul, entries[0].RawDataOffset);
            Assert.Equal(0x50000ul, entries[0].RawDataSize);
            Assert.Equal(0x50000ul, entries[1].RawDataOffset);
            Assert.Equal(0x100000ul, entries[1].RawDataSize);
        }
    }

    [Fact]
    public void ParseRawDataEntries_NoEntries_ReturnsEmpty()
    {
        var disc = new TestDiscBuilder { NumRawDataEntries = 0 };
        var (file, discBytes) = BuildFile(disc, [], [], []);
        using (file)
        {
            Assert.Empty(TableParser.ParseRawDataEntries(file, WiaDisc.Parse(discBytes)));
        }
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Lzma2)]
    public void ParseGroupEntries_EveryCodec(CompressionType compression)
    {
        var groupTable = Concat(
            MakeGroupEntry(0x100, 0x2000, 0),
            MakeGroupEntry(0x900, 0x80000000 | 0x1000, 0x1800), // compressed flag + packing
            MakeGroupEntry(0x1000, 0, 0)); // all-zero group
        var stored = TestCompressor.Compress(compression, groupTable);
        var disc = new TestDiscBuilder { NumGroups = 3 };
        ConfigureCompression(disc, compression);
        var (file, discBytes) = BuildFile(disc, [], [], stored);
        using (file)
        {
            var entries = TableParser.ParseGroupEntries(file, WiaDisc.Parse(discBytes));

            Assert.Equal(3, entries.Length);
            Assert.Equal(0x400ul, entries[0].FileOffset); // 0x100 << 2
            Assert.False(entries[0].UsesDiscCompression);
            Assert.Equal(0x2000u, entries[0].StoredSize);
            Assert.True(entries[1].UsesDiscCompression);
            Assert.Equal(0x1000u, entries[1].StoredSize);
            Assert.Equal(0x1800u, entries[1].RvzPackedSize);
            Assert.Equal(0u, entries[2].StoredSize);
        }
    }

    [Fact]
    public void ParseGroupEntries_TruncatedDecompression_Throws()
    {
        var compressed = TestCompressor.Compress(CompressionType.Zstd,
            MakeGroupEntry(0x100, 0x2000, 0))[..10]; // cut short
        var disc = new TestDiscBuilder { NumGroups = 1 };
        ConfigureCompression(disc, CompressionType.Zstd);
        var (file, discBytes) = BuildFile(disc, [], [], compressed);
        using (file)
        {
            Assert.Throws<RvzFormatException>(() =>
                TableParser.ParseGroupEntries(file, WiaDisc.Parse(discBytes)));
        }
    }
}
