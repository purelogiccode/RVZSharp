using System.Security.Cryptography;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

public class WiiHashCalculatorTests
{
    private static byte[] SectorData(int seed = 1)
    {
        var data = new byte[WiiHashCalculator.SectorDataSize];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] ComputeHashArea(byte[] data)
    {
        var hashArea = new byte[WiiHashCalculator.HashBlockSize];
        WiiHashCalculator.BuildHashArea(data, hashArea);
        return hashArea;
    }

    [Fact]
    public void HashAreaLayout_MatchesTheWiiHashTree()
    {
        var data = SectorData(1);
        var hashArea = ComputeHashArea(data);

        // h0: SHA-1 of each 0x400 data block, 31 entries starting at 0.
        for (var block = 0; block < WiiHashCalculator.BlocksPerSector; block++)
        {
            var expected = SHA1.HashData(
                data.AsSpan(block * WiiHashCalculator.BlockDataSize, WiiHashCalculator.BlockDataSize));
            Assert.Equal(expected, hashArea.AsSpan(block * WiiHashCalculator.HashSize, WiiHashCalculator.HashSize).ToArray());
        }

        // h0 padding 0x26C-0x280 is zero.
        Assert.All(hashArea.AsSpan(WiiHashCalculator.H0Size, 0x14).ToArray(), b => Assert.Equal(0, b));

        // h1: SHA-1 of the h0 area (slot 0 of the group's shared array at 0x280).
        var expectedH1 = SHA1.HashData(hashArea.AsSpan(0, WiiHashCalculator.H0Size));
        Assert.Equal(expectedH1, hashArea.AsSpan(0x280, WiiHashCalculator.HashSize).ToArray());
        // h1 padding 0x320-0x340 is zero.
        Assert.All(hashArea.AsSpan(0x320, 0x20).ToArray(), b => Assert.Equal(0, b));

        // h2: SHA-1 of the h1 array (slot (sector / 8) of the shared h2 at 0x340).
        var expectedH2 = SHA1.HashData(hashArea.AsSpan(0x280, 0xA0));
        Assert.Equal(expectedH2, hashArea.AsSpan(0x340, WiiHashCalculator.HashSize).ToArray());
        // h2 padding 0x3E0-0x400 is zero.
        Assert.All(hashArea.AsSpan(0x3E0, 0x20).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ZeroSectorH0Area_Is31TimesTheZeroBlockHash()
    {
        var zeroBlock = SHA1.HashData(new byte[WiiHashCalculator.BlockDataSize]);
        var expected = new byte[WiiHashCalculator.H0Size];
        for (var i = 0; i < WiiHashCalculator.BlocksPerSector; i++)
        {
            zeroBlock.CopyTo(expected, i * WiiHashCalculator.HashSize);
        }

        Assert.Equal(expected, WiiHashCalculator.ZeroSectorH0Area);
    }

    [Fact]
    public void BuildHashArea_RejectsWrongLengths()
    {
        var hashArea = new byte[WiiHashCalculator.HashBlockSize];

        Assert.Throws<ArgumentException>(() => WiiHashCalculator.BuildHashArea(new byte[0x7BFF], hashArea));
        Assert.Throws<ArgumentException>(() => WiiHashCalculator.BuildHashArea(new byte[0x7C01], hashArea));
        Assert.Throws<ArgumentException>(() => WiiHashCalculator.BuildHashArea(new byte[WiiHashCalculator.SectorDataSize], new byte[0x3FF]));
    }

    [Fact]
    public void ApplyHashExceptions_WritesTheGivenHashes()
    {
        var hashArea = new byte[WiiHashCalculator.HashBlockSize];
        var hashA = Enumerable.Range(0, 20).Select(i => (byte)(0xA0 + i)).ToArray();
        var hashB = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var exceptions = new[]
        {
            new HashExceptionEntry(0x300, hashA),
            new HashExceptionEntry(0x104, hashB)
        };

        WiiHashCalculator.ApplyHashExceptions(exceptions, hashArea);

        Assert.Equal(hashA, hashArea.AsSpan(0x300, 20).ToArray());
        Assert.Equal(hashB, hashArea.AsSpan(0x104, 20).ToArray());
    }

    [Fact]
    public void ApplyHashExceptions_ChunkBaseOffset_ShiftsTheOffsets()
    {
        var hashArea = new byte[WiiHashCalculator.HashBlockSize];
        var hash = Enumerable.Range(0, 20).Select(i => (byte)(i + 1)).ToArray();

        WiiHashCalculator.ApplyHashExceptions([new HashExceptionEntry(0x100, hash)], hashArea,
            chunkBaseOffset: 0x100);

        // 0x100 (base) + 0x100 (exception) = 0x200.
        Assert.Equal(hash, hashArea.AsSpan(0x200, 20).ToArray());
    }

    [Theory]
    [InlineData(0x3F0)] // starts at the very end: 20 bytes would overflow the 0x400 area
    [InlineData(0x400)] // beyond the sector hash area entirely
    [InlineData(0xFFFF)]
    public void ApplyHashExceptions_OutOfRange_Throws(ushort offset)
    {
        var hashArea = new byte[WiiHashCalculator.HashBlockSize];
        var exceptions = new[] { new HashExceptionEntry(offset, new byte[20]) };

        Assert.Throws<RvzFormatException>(() => WiiHashCalculator.ApplyHashExceptions(exceptions, hashArea));
    }

    [Fact]
    public void ApplyHashExceptions_ChunkBase_PushesOffsetsOutOfRange()
    {
        var hashArea = new byte[WiiHashCalculator.HashBlockSize];
        var exceptions = new[] { new HashExceptionEntry(0x300, new byte[20]) };

        Assert.Throws<RvzFormatException>(() =>
            WiiHashCalculator.ApplyHashExceptions(exceptions, hashArea, chunkBaseOffset: 0x200));
    }
}