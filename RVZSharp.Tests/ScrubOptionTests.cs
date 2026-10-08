using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for <see cref="RvzWriteOptions.Scrub"/>: the writer zeroes the data of non-game
/// Wii partitions (Dolphin: DiscScrubber) while keeping the game partition byte-exact.
/// </summary>
public class ScrubOptionTests
{
    private const int DataOffset = 0x40000;
    private const int PartitionSize = 0x40000;
    private const int GamePartition = 0x100000;
    private const int UpdatePartition = 0x220000;
    private const int GameDataStart = GamePartition + DataOffset;
    private const int UpdateDataStart = UpdatePartition + DataOffset;
    private const int ImageSize = UpdateDataStart + PartitionSize + 0x10000;

    /// <summary>A Wii disc with a game partition (0xAA data) and an update partition (0x22).</summary>
    private static byte[] BuildWiiIso()
    {
        var iso = new byte[ImageSize];
        Array.Fill(iso, (byte)0x33);
        WriteBe32(iso, 0x18, WiiVolume.WII_MAGIC);
        iso[0x60] = 0; // hashes present
        iso[0x61] = 0; // encrypted

        // Partition table groups at 0x40000 (4 × { count, table offset }); group 0 has the
        // entries, the unused groups are empty.
        const int tableAddress = 0x40020;
        WriteBe32(iso, 0x40000, 2);
        WriteBe32(iso, 0x40004, tableAddress >> 2);
        for (var group = 1; group < 4; group++)
        {
            WriteBe32(iso, 0x40000 + group * 8, 0);
            WriteBe32(iso, 0x40004 + group * 8, 0);
        }

        WriteBe32(iso, tableAddress, (uint)(GamePartition >> 2));
        WriteBe32(iso, tableAddress + 4, 0);
        WriteBe32(iso, tableAddress + 8, (uint)(UpdatePartition >> 2));
        WriteBe32(iso, tableAddress + 12, 1);
        WritePartitionHeader(iso, GamePartition);
        WritePartitionHeader(iso, UpdatePartition);

        Array.Fill(iso, (byte)0xAA, GameDataStart, PartitionSize);
        Array.Fill(iso, (byte)0x22, UpdateDataStart, PartitionSize);
        return iso;
    }

    private static void WritePartitionHeader(byte[] iso, int offset)
    {
        WriteBe32(iso, offset, 0x10001); // RSA2048 ticket signature
        WriteBe32(iso, offset + 0x2B8, DataOffset >> 2);
        WriteBe32(iso, offset + 0x2BC, PartitionSize >> 2);
    }

    private static byte[] WriteAndDecode(byte[] iso, bool scrub)
    {
        using var input = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        using var output = new MemoryStream();
        RvzWriter.Write(input, output,
            new RvzWriteOptions { Compression = CompressionType.None, Scrub = scrub });

        output.Position = 0;
        using var reader = RvzReader.Open(output, leaveOpen: true);
        return reader.ReadFully();
    }

    [Fact]
    public void WithoutScrub_UpdatePartitionRoundTrips()
    {
        var iso = BuildWiiIso();
        var decoded = WriteAndDecode(iso, scrub: false);

        Assert.Equal(iso.AsSpan(GameDataStart, PartitionSize).ToArray(),
            decoded.AsSpan(GameDataStart, PartitionSize).ToArray());
        Assert.Equal(iso.AsSpan(UpdateDataStart, PartitionSize).ToArray(),
            decoded.AsSpan(UpdateDataStart, PartitionSize).ToArray());
    }

    [Fact]
    public void WithScrub_UpdatePartitionIsZeroed_GamePartitionSurvives()
    {
        var iso = BuildWiiIso();
        var decoded = WriteAndDecode(iso, scrub: true);

        // The game partition data survives byte-for-byte.
        Assert.Equal(iso.AsSpan(GameDataStart, PartitionSize).ToArray(),
            decoded.AsSpan(GameDataStart, PartitionSize).ToArray());

        // The update partition data is zeroed by the scrub.
        Assert.All(decoded.AsSpan(UpdateDataStart, PartitionSize).ToArray(),
            b => Assert.Equal(0, b));
    }

    [Fact]
    public void Scrub_OnGameCubeDisc_IsANoOp()
    {
        var iso = new byte[0x10000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        var decoded = WriteAndDecode(iso, scrub: true);
        Assert.Equal(iso, decoded);
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
