using RVZSharp.Chunks;
using RVZSharp.Tests.Helpers;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

public class PartitionRegionBuilderTests
{
    private static byte[] SectorData(int sector, int seedBase)
    {
        var data = new byte[0x7C00];
        var rng = new Random(seedBase + sector * 7919);
        rng.NextBytes(data);
        return data;
    }

    private static byte[] RegionData(int sectorCount, int seedBase)
    {
        var data = new byte[sectorCount * 0x7C00];
        for (var s = 0; s < sectorCount; s++)
        {
            SectorData(s, seedBase).CopyTo(data, s * 0x7C00);
        }

        return data;
    }

    private static HashExceptionEntry[][] BuildExceptions(int sectorCount)
    {
        var exceptions = new HashExceptionEntry[sectorCount][];
        for (var s = 0; s < sectorCount; s++)
        {
            var list = new List<HashExceptionEntry>();
            if (s % 5 == 0)
            {
                // One exception inside the h0 area of this sector (region-relative offset).
                var hash = new byte[20];
                new Random(s).NextBytes(hash);
                list.Add(new HashExceptionEntry(100, hash)); // sector-relative offset
            }

            if (s % 7 == 0)
            {
                // One exception inside the h2 area.
                var hash = new byte[20];
                new Random(s + 1).NextBytes(hash);
                list.Add(new HashExceptionEntry(0x370, hash)); // sector-relative offset
            }

            exceptions[s] = [.. list];
        }

        return exceptions;
    }

    [Fact]
    public void FullRegion_MatchesReference()
    {
        var data = RegionData(64, seedBase: 11);
        var key = new byte[16];
        new Random(42).NextBytes(key);
        var exceptions = BuildExceptions(64);

        var builder = new PartitionRegionBuilder(key);
        for (var s = 0; s < 64; s++)
        {
            builder.AddSector(data.AsSpan(s * 0x7C00, 0x7C00), exceptions[s]);
        }

        var actual = builder.Finish();
        var expected = ReferenceWiiRegion.Build(data, key, exceptions, 64);

        Assert.Equal(64 * 0x8000, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PartialRegion_ZeroPadding_MatchesReference()
    {
        // 60 real sectors: h1/h2 for the last group hash zero-filled sectors (Dolphin semantics).
        var data = RegionData(60, seedBase: 5);
        var key = new byte[16];
        new Random(7).NextBytes(key);
        var exceptions = BuildExceptions(60);

        var builder = new PartitionRegionBuilder(key);
        for (var s = 0; s < 60; s++)
        {
            builder.AddSector(data.AsSpan(s * 0x7C00, 0x7C00), exceptions[s]);
        }

        var actual = builder.Finish();
        var expected = ReferenceWiiRegion.Build(data, key, exceptions, 60);

        Assert.Equal(60 * 0x8000, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NoExceptions_MatchesReference()
    {
        var data = RegionData(64, seedBase: 3);
        var key = new byte[16];
        new Random(9).NextBytes(key);
        var exceptions = new HashExceptionEntry[64][];
        for (var i = 0; i < 64; i++)
        {
            exceptions[i] = [];
        }

        var builder = new PartitionRegionBuilder(key);
        for (var s = 0; s < 64; s++)
        {
            builder.AddSector(data.AsSpan(s * 0x7C00, 0x7C00), exceptions[s]);
        }

        Assert.Equal(ReferenceWiiRegion.Build(data, key, exceptions, 64), builder.Finish());
    }

    [Fact]
    public void HashAreaLayout_MatchesDolphinOffsets()
    {
        // Verify the unencrypted hash area layout via the reference: decrypt the first sector's
        // hash area and check that h1/h2/padding sit at the documented offsets.
        var data = RegionData(64, seedBase: 1);
        var key = new byte[16];
        new Random(2).NextBytes(key);
        var exceptions = new HashExceptionEntry[64][];
        for (var i = 0; i < 64; i++)
        {
            exceptions[i] = [];
        }

        var builder = new PartitionRegionBuilder(key);
        for (var s = 0; s < 64; s++)
        {
            builder.AddSector(data.AsSpan(s * 0x7C00, 0x7C00), exceptions[s]);
        }

        var encrypted = builder.Finish();

        // Decrypt the hash area of sector 0 with a zero IV.
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var hashArea = decryptor.TransformFinalBlock(encrypted, 0, 0x400);

        // h0[0] must be SHA-1 of the first data block; h1[0] = SHA-1 of the h0 area;
        // h2[0] = SHA-1 of the h1 area; paddings must be zero.
        var expectedH0 = System.Security.Cryptography.SHA1.HashData(data.AsSpan(0, 0x400));
        Assert.Equal(expectedH0, hashArea.AsSpan(0, 20).ToArray());
        Assert.All(hashArea.AsSpan(0x26C, 0x14).ToArray(), b => Assert.Equal(0, b));

        var expectedH1 = System.Security.Cryptography.SHA1.HashData(hashArea.AsSpan(0, 0x26C));
        Assert.Equal(expectedH1, hashArea.AsSpan(0x280, 20).ToArray());
        Assert.All(hashArea.AsSpan(0x320, 0x20).ToArray(), b => Assert.Equal(0, b));

        var expectedH2 = System.Security.Cryptography.SHA1.HashData(hashArea.AsSpan(0x280, 0xA0));
        Assert.Equal(expectedH2, hashArea.AsSpan(0x340, 20).ToArray());
        Assert.All(hashArea.AsSpan(0x3E0, 0x20).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void PartialRegion_ZeroSectorHashes_MatchFirstPrinciples()
    {
        // 60 real sectors: the h1 entries for sectors 60-63 (group 7) must hash the h0 area
        // of an all-zero sector — zero data hashed normally (31 × SHA1(0x400 zeros)), not a
        // raw zero buffer (Dolphin: VolumeWii.cpp EncryptGroup; Go: part.go DevZero).
        var data = RegionData(60, seedBase: 5);
        var key = new byte[16];
        new Random(7).NextBytes(key);

        var builder = new PartitionRegionBuilder(key);
        for (var s = 0; s < 60; s++)
        {
            builder.AddSector(data.AsSpan(s * 0x7C00, 0x7C00), []);
        }

        var encrypted = builder.Finish();

        // The h1 array is shared within each 8-sector group (Dolphin: "H1 copies"), so group
        // 7's h1 lives in sectors 56-63 — decrypt sector 56's hash area (zero IV).
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var hashArea = decryptor.TransformFinalBlock(encrypted, 56 * 0x8000, 0x400);

        var zeroBlockHash = System.Security.Cryptography.SHA1.HashData(new byte[0x400]);
        var zeroSectorH0 = new byte[31 * 20];
        for (var k = 0; k < 31; k++)
        {
            zeroBlockHash.CopyTo(zeroSectorH0, k * 20);
        }

        var expected = System.Security.Cryptography.SHA1.HashData(zeroSectorH0);
        Assert.NotEqual(expected,
            System.Security.Cryptography.SHA1.HashData(new byte[31 * 20])); // the old (wrong) convention
        // Group 7's h1 entries for the first zero-filled sectors (60 and 63).
        Assert.Equal(expected, hashArea.AsSpan(0x280 + (60 % 8) * 20, 20).ToArray());
        Assert.Equal(expected, hashArea.AsSpan(0x280 + (63 % 8) * 20, 20).ToArray());
        // The last group's h2 covers an h1 array that includes those entries.
        Assert.Equal(System.Security.Cryptography.SHA1.HashData(hashArea.AsSpan(0x280, 0xA0)),
            hashArea.AsSpan(0x340 + 7 * 20, 20).ToArray());
    }

    [Fact]
    public void Builder_TooManySectors_Throws()
    {
        var builder = new PartitionRegionBuilder(new byte[16]);
        for (var s = 0; s < 64; s++)
        {
            builder.AddSector(new byte[0x7C00], []);
        }

        Assert.Throws<InvalidOperationException>(() => builder.AddSector(new byte[0x7C00], []));
    }

    [Fact]
    public void Builder_EmptyRegion_ReturnsEmpty()
    {
        var builder = new PartitionRegionBuilder(new byte[16]);
        Assert.Empty(builder.Finish());
    }

    [Fact]
    public void Builder_BadKeyLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PartitionRegionBuilder(new byte[15]));
    }
}
