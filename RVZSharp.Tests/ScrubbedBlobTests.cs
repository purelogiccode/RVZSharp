using RVZSharp.Blobs;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

public class ScrubbedBlobTests
{
    private const int DataOffset = 0x40000;
    private const int PartitionSize = 0x40000;
    private const int GamePartition = 0x100000;
    private const int UpdatePartition = 0x220000;
    private const int GameDataStart = GamePartition + DataOffset;
    private const int UpdateDataStart = UpdatePartition + DataOffset;
    private const int ImageSize = UpdateDataStart + PartitionSize + 0x10000;

    /// <summary>
    /// A Wii disc with a game partition (type 0) and an update partition (type 1);
    /// their data areas are filled with 0xAA and 0x22, everything else with 0x33.
    /// </summary>
    private static byte[] BuildWiiIso(params (int Offset, int Type)[] partitions)
    {
        var iso = new byte[ImageSize];
        Array.Fill(iso, (byte)0x33);
        WriteBe32(iso, 0x18, WiiVolume.WII_MAGIC);

        // Partition table group 0 at 0x40000: { count, table offset }, entries { offset, type }.
        const int tableAddress = 0x40008;
        WriteBe32(iso, 0x40000, (uint)partitions.Length);
        WriteBe32(iso, 0x40004, tableAddress >> 2);
        for (var i = 0; i < partitions.Length; i++)
        {
            WriteBe32(iso, tableAddress + i * 8, (uint)(partitions[i].Offset >> 2));
            WriteBe32(iso, tableAddress + i * 8 + 4, (uint)partitions[i].Type);
            WritePartitionHeader(iso, partitions[i].Offset);
        }

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

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static PlainBlob OpenDisc(byte[] iso)
    {
        return PlainBlob.Open(new MemoryStream(iso));
    }

    [Fact]
    public void Create_ScrubsNonGamePartitions_And_KeepsTheRest()
    {
        var iso = BuildWiiIso((GamePartition, 0), (UpdatePartition, 1));
        using var blob = OpenDisc(iso);
        using var scrubbed = Assert.IsType<ScrubbedBlob>(ScrubbedBlob.Create(blob));

        // Game partition data is served unchanged.
        var game = new byte[PartitionSize];
        Assert.Equal(PartitionSize, scrubbed.ReadAt(GameDataStart, game));
        Assert.All(game, b => Assert.Equal(0xAA, b));

        // Update partition data is scrubbed to zeroes.
        var update = new byte[PartitionSize];
        Assert.Equal(PartitionSize, scrubbed.ReadAt(UpdateDataStart, update));
        Assert.All(update, b => Assert.Equal(0, b));

        // Everything else is untouched (the header, including the Wii magic at 0x18).
        var header = new byte[0x80];
        Assert.Equal(0x80, scrubbed.ReadAt(0, header));
        Assert.Equal(iso[..0x80], header);
    }

    [Fact]
    public void Create_NonWiiDisc_ReturnsNull()
    {
        var iso = BuildWiiIso((GamePartition, 0), (UpdatePartition, 1));
        WriteBe32(iso, 0x18, WiiVolume.GC_MAGIC);

        using var blob = OpenDisc(iso);
        Assert.Null(ScrubbedBlob.Create(blob));
    }

    [Fact]
    public void Create_WiiDiscWithoutGamePartition_ReturnsNull()
    {
        var iso = BuildWiiIso((UpdatePartition, 1));

        using var blob = OpenDisc(iso);
        Assert.Null(ScrubbedBlob.Create(blob));
    }

    [Fact]
    public void ReadAt_SpanningAScrubBoundary_ReturnsMix()
    {
        var iso = BuildWiiIso((GamePartition, 0), (UpdatePartition, 1));
        using var blob = OpenDisc(iso);
        using var scrubbed = ScrubbedBlob.Create(blob)!;

        // [updateStart - 16, updateStart + 32): unscrubbed 0x33 gap bytes, then zeroes.
        var buffer = new byte[48];
        Assert.Equal(buffer.Length, scrubbed.ReadAt(UpdateDataStart - 16, buffer));
        Assert.All(buffer[..16], b => Assert.Equal(0x33, b));
        Assert.All(buffer[16..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ReadAt_OutOfRange_And_EmptyBuffer_ReturnZero()
    {
        var iso = BuildWiiIso((GamePartition, 0), (UpdatePartition, 1));
        using var blob = OpenDisc(iso);
        using var scrubbed = ScrubbedBlob.Create(blob)!;

        Assert.Equal(0, scrubbed.ReadAt(iso.Length, new byte[16]));
        Assert.Equal(0, scrubbed.ReadAt(-1, new byte[16]));
        Assert.Equal(0, scrubbed.ReadAt(0, Span<byte>.Empty));
    }

    [Fact]
    public void Metadata_MirrorTheInnerBlob()
    {
        var iso = BuildWiiIso((GamePartition, 0), (UpdatePartition, 1));
        using var blob = OpenDisc(iso);
        using var scrubbed = ScrubbedBlob.Create(blob)!;

        Assert.Equal(blob.Type, scrubbed.Type);
        Assert.Equal(blob.Length, scrubbed.Length);
        Assert.Equal(blob.BlockSize, scrubbed.BlockSize);
    }

    [Fact]
    public void Dispose_ClosesTheInnerBlob()
    {
        var iso = BuildWiiIso((GamePartition, 0), (UpdatePartition, 1));
        using var blob = OpenDisc(iso);
        var scrubbed = ScrubbedBlob.Create(blob)!;

        scrubbed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => blob.ReadAt(0, new byte[4]));
    }
}