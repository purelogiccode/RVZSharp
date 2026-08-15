using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class WiaDiscTests
{
    [Fact]
    public void Parse_ValidDisc_ReturnsAllFields()
    {
        var header = new byte[WiaDisc.DiscHeaderSize];
        header[0] = 0x5D;
        var builder = new TestDiscBuilder
        {
            DiscType = DiscType.Wii,
            Compression = CompressionType.Zstd,
            ComprLevel = -5,
            ChunkSize = 0x200000,
            DiscHeader = header,
            NumPartitions = 2,
            PartitionEntrySize = 0x30,
            PartitionEntriesOffset = 0x1000,
            NumRawDataEntries = 3,
            RawDataEntriesOffset = 0x2000,
            RawDataEntriesSize = 0x30,
            NumGroups = 4,
            GroupEntriesOffset = 0x3000,
            GroupEntriesSize = 0x40,
            ComprDataLen = 0
        };
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);

        Assert.Equal(DiscType.Wii, disc.DiscType);
        Assert.Equal(CompressionType.Zstd, disc.Compression);
        Assert.Equal(-5, disc.ComprLevel);
        Assert.Equal(0x200000u, disc.ChunkSize);
        Assert.Equal(0x5D, disc.DiscHeader[0]);
        Assert.Equal(2u, disc.NumPartitions);
        Assert.Equal(0x30u, disc.PartitionEntrySize);
        Assert.Equal(0x1000ul, disc.PartitionEntriesOffset);
        Assert.Equal(3u, disc.NumRawDataEntries);
        Assert.Equal(0x2000ul, disc.RawDataEntriesOffset);
        Assert.Equal(0x30u, disc.RawDataEntriesSize);
        Assert.Equal(4u, disc.NumGroups);
        Assert.Equal(0x3000ul, disc.GroupEntriesOffset);
        Assert.Equal(0x40u, disc.GroupEntriesSize);
        Assert.Equal(0, disc.ComprDataLen);
    }

    [Fact]
    public void Parse_Truncated_ThrowsFormatException()
    {
        var bytes = new TestDiscBuilder().Build();
        Assert.Throws<RvzFormatException>(() => WiaDisc.Parse(bytes.AsSpan(0, 0xD4)));
    }

    [Fact]
    public void Parse_ZeroFillsMissingComprData()
    {
        var bytes = new TestDiscBuilder().Build();
        var disc = WiaDisc.Parse(bytes.AsSpan(0, WiaDisc.MinSize)); // 0xD5, compr_data absent
        Assert.Equal(0, disc.ComprDataLen);
        Assert.All(disc.ComprData, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Validate_ValidDisc_Passes()
    {
        var builder = new TestDiscBuilder { DiscType = DiscType.GameCube, Compression = CompressionType.None };
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        disc.Validate((uint)bytes.Length, bytes, builder.GetDiscHash());
    }

    [Fact]
    public void Validate_BadDiscHash_ThrowsHashMismatchException()
    {
        var builder = new TestDiscBuilder();
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        Assert.Throws<RvzHashMismatchException>(() =>
            disc.Validate((uint)bytes.Length, bytes, new byte[WiaDisc.HashSize]));
    }

    [Fact]
    public void Validate_NonstandardDiscType_IsAccepted()
    {
        // Dolphin never validates disc_type on read (WIABlob.cpp) — nonstandard values are
        // accepted (0 is what Dolphin's converter writes for unrecognized volumes).
        var builder = new TestDiscBuilder { DiscType = (DiscType)99 };
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        disc.Validate((uint)bytes.Length, bytes, builder.GetDiscHash());
        Assert.Equal((DiscType)99, disc.DiscType);
    }

    [Fact]
    public void Validate_PurgeCompression_ThrowsUnsupportedException()
    {
        var builder = new TestDiscBuilder { Compression = CompressionType.Purge };
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        Assert.Throws<RvzUnsupportedException>(() =>
            disc.Validate((uint)bytes.Length, bytes, builder.GetDiscHash()));
    }

    [Fact]
    public void Validate_UnknownCompression_ThrowsUnsupportedException()
    {
        var builder = new TestDiscBuilder { Compression = (CompressionType)42 };
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        Assert.Throws<RvzUnsupportedException>(() =>
            disc.Validate((uint)bytes.Length, bytes, builder.GetDiscHash()));
    }

    [Theory]
    [InlineData(0x8000)]  // 32 KiB — min power of two, valid
    [InlineData(0x10000)] // 64 KiB
    [InlineData(0x200000)] // 2 MiB
    [InlineData(0x400000)] // 4 MiB (multiple of 2 MiB, not power of two)
    public void Validate_ChunkSizes_Valid(uint chunkSize)
    {
        var builder = new TestDiscBuilder { ChunkSize = chunkSize };
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        disc.Validate((uint)bytes.Length, bytes, builder.GetDiscHash());
    }

    [Theory]
    [InlineData(0x1000)]  // too small
    [InlineData(0x18000)] // not a power of two and not a multiple of 2 MiB
    [InlineData(0x300000)] // 3 MiB: multiple of 2 MiB? no
    public void Validate_ChunkSizes_Invalid(uint chunkSize)
    {
        var builder = new TestDiscBuilder { ChunkSize = chunkSize };
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        Assert.Throws<RvzFormatException>(() =>
            disc.Validate((uint)bytes.Length, bytes, builder.GetDiscHash()));
    }

    [Fact]
    public void Validate_DiscSizeTooSmall_ThrowsFormatException()
    {
        var builder = new TestDiscBuilder();
        var bytes = builder.Build();

        var disc = WiaDisc.Parse(bytes);
        Assert.Throws<RvzFormatException>(() =>
            disc.Validate(WiaDisc.MinSize - 1, bytes, builder.GetDiscHash()));
    }

    [Fact]
    public void Validate_ComprDataOverflow_ThrowsFormatException()
    {
        var builder = new TestDiscBuilder { ComprDataLen = 7 };
        var bytes = builder.Build();
        var disc = WiaDisc.Parse(bytes);

        // disc size 0xD5 with compr_data_len 7 → 0xD5 + 7 > 0xD5 → invalid
        Assert.Throws<RvzFormatException>(() =>
            disc.Validate(WiaDisc.MinSize, bytes.AsSpan(0, WiaDisc.MinSize), builder.GetDiscHash()));
    }
}
