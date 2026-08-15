using RVZSharp.Blobs;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

public class WiiPartitionExtractorTests
{
    private static byte[] RegionData(int sectorCount, int seedBase = 3)
    {
        var data = new byte[sectorCount * WiiHashCalculator.SectorDataSize];
        for (var sector = 0; sector < sectorCount; sector++)
        {
            new Random(seedBase + sector * 7919).NextBytes(
                data.AsSpan(sector * WiiHashCalculator.SectorDataSize, WiiHashCalculator.SectorDataSize));
        }

        return data;
    }

    /// <summary>Encrypts <paramref name="sectorCount"/> sectors like the disc does.</summary>
    private static byte[] BuildEncryptedRegion(byte[] key, byte[] data, int sectorCount)
    {
        var builder = new PartitionRegionBuilder(key);
        for (var sector = 0; sector < sectorCount; sector++)
        {
            builder.AddSector(data.AsSpan(sector * WiiHashCalculator.SectorDataSize, WiiHashCalculator.SectorDataSize), []);
        }

        return builder.Finish();
    }

    /// <summary>Places an encrypted region at <paramref name="discOffset"/> inside a disc image.</summary>
    private static PlainBlob CreateDisc(byte[] encrypted, int discOffset)
    {
        var disc = new byte[discOffset + encrypted.Length];
        encrypted.CopyTo(disc, discOffset);
        return PlainBlob.Open(new MemoryStream(disc));
    }

    private static WiiPartitionExtractor CreateExtractor(PlainBlob blob, byte[] key)
    {
        return new WiiPartitionExtractor(blob, key);
    }

    private static byte[] Key(int seed = 42)
    {
        var key = new byte[16];
        new Random(seed).NextBytes(key);
        return key;
    }

    [Fact]
    public void PristineRegion_DecryptsData_And_FindsNoExceptions()
    {
        var data = RegionData(64);
        var key = Key();
        var encrypted = BuildEncryptedRegion(key, data, 64);

        using var blob = CreateDisc(encrypted, 0);
        var extractor = CreateExtractor(blob, key);
        var (decrypted, exceptions) = extractor.ExtractRegion(0, 64);

        Assert.Equal(data, decrypted);
        Assert.Empty(exceptions);
    }

    [Fact]
    public void PartialRegion_RoundTrips_WithZeroFilledTailConvention()
    {
        // 60 real sectors: the h1/h2 for the missing 4 sectors hash zero-filled h0 areas, and
        // the extractor must reproduce the same convention (no spurious exceptions).
        var data = RegionData(60);
        var key = Key();
        var encrypted = BuildEncryptedRegion(key, data, 60);

        using var blob = CreateDisc(encrypted, 0);
        var extractor = CreateExtractor(blob, key);
        var (decrypted, exceptions) = extractor.ExtractRegion(0, 60);

        Assert.Equal(data, decrypted);
        Assert.Empty(exceptions);
    }

    [Fact]
    public void RegionAtDiscOffset_ReadsFromTheRightPosition()
    {
        var data = RegionData(8);
        var key = Key();
        var encrypted = BuildEncryptedRegion(key, data, 8);

        using var blob = CreateDisc(encrypted, 0x800000);
        var extractor = CreateExtractor(blob, key);
        var (decrypted, exceptions) = extractor.ExtractRegion(0x800000, 8);

        Assert.Equal(data, decrypted);
        Assert.Empty(exceptions);
    }

    [Fact]
    public void CorruptedHashBytes_ProduceExceptions_WithoutTouchingData()
    {
        var data = RegionData(64);
        var key = Key();
        var encrypted = BuildEncryptedRegion(key, data, 64);

        // Flip one full AES block (16 bytes at ciphertext 0x30) of sector 4's hash area. CBC
        // decryption propagates the change to the plaintext windows at 0x28..0x4F, which the
        // compare walks as 20-byte windows → exactly two exceptions (offsets 0x28 and 0x3C).
        const int sector = 4;
        const int start = sector * 0x8000 + 0x30;
        for (var i = 0; i < 16; i++)
        {
            encrypted[start + i] ^= 0xFF;
        }

        using var blob = CreateDisc(encrypted, 0);
        var extractor = CreateExtractor(blob, key);
        var (decrypted, exceptions) = extractor.ExtractRegion(0, 64);

        Assert.Equal(data, decrypted);
        Assert.Equal(
            new ushort[] { sector * 0x400 + 0x28, sector * 0x400 + 0x3C },
            exceptions.Select(e => e.Offset).ToArray());
        Assert.All(exceptions, e => Assert.Equal(20, e.Hash.Length));
    }

    [Fact]
    public void WrongKey_DecryptsToDifferentData()
    {
        var data = RegionData(4);
        var encrypted = BuildEncryptedRegion(Key(1), data, 4);

        using var blob = CreateDisc(encrypted, 0);
        var extractor = CreateExtractor(blob, Key(2));
        var (decrypted, exceptions) = extractor.ExtractRegion(0, 4);

        Assert.NotEqual(data, decrypted);
        Assert.NotEmpty(exceptions);
    }

    [Fact]
    public void ShortInput_Throws()
    {
        using var blob = CreateDisc(new byte[0x8000], 0);
        var extractor = CreateExtractor(blob, Key());
        Assert.Throws<InvalidOperationException>(() => extractor.ExtractRegion(0, 2));
    }
}