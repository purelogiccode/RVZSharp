using System.Text;
using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

/// <summary>
/// Edge-case tests for <see cref="DiscInfo"/>: header validation, the GameCube/Wii region
/// words, Dolphin's country-code mapping and its region fallback, internal-name decoding
/// and the Wii title ID read from the game partition's ticket.
/// </summary>
public class DiscInfoEdgeCaseTests
{
    /// <summary>Verifies that try read rejects a null reader.</summary>
    [Fact]
    public void TryRead_NullReader_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DiscInfo.TryRead(null!));
    }

    /// <summary>Verifies that try read returns null when the image is shorter than a disc header.</summary>
    [Fact]
    public void TryRead_TooShort_ReturnsNull()
    {
        using var blob = PlainBlob.Open(new MemoryStream(new byte[0x40]));
        Assert.Null(DiscInfo.TryRead(blob));
    }

    /// <summary>Verifies that try read returns null and read throws when there is no disc magic.</summary>
    [Fact]
    public void TryRead_NoMagic_ReturnsNull_AndReadThrows()
    {
        var data = new byte[0x1000];
        new Random(1).NextBytes(data);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        Assert.Null(DiscInfo.TryRead(blob));
        Assert.Throws<RvzFormatException>(() => DiscInfo.Read(blob));
    }

    /// <summary>Verifies that read returns the metadata for a valid disc.</summary>
    [Fact]
    public void Read_ValidDisc_ReturnsMetadata()
    {
        var data = BuildDisc(wii: false, code: "GAL", country: 'E', maker: "01",
            revision: 2, name: "TEST GAME", regionWord: 1);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        var info = DiscInfo.Read(blob);

        Assert.Equal(DiscType.GameCube, info.DiscType);
        Assert.Equal("GALE01", info.GameId);
        Assert.Equal("01", info.MakerId);
        Assert.Equal((byte)2, info.Revision);
        Assert.Equal("TEST GAME", info.InternalName);
        Assert.Equal("NTSC-U", info.Region);
        Assert.Equal('E', info.CountryCode);
        Assert.Equal("USA", info.Country);
        Assert.Null(info.TitleId);
    }

    /// <summary>Verifies that the internal name is decoded as CP1252 for non-Japanese discs.</summary>
    [Fact]
    public void GameCube_InternalName_DecodesCp1252()
    {
        var data = BuildDisc(wii: false, code: "GAL", country: 'E', maker: "01", regionWord: 1);
        // 0xE9 is 'é' in CP1252 (0x82 is its Shift-JIS lead byte).
        Encoding.ASCII.GetBytes("Caf").CopyTo(data, 0x20);
        data[0x23] = 0xE9;
        using var blob = PlainBlob.Open(new MemoryStream(data));

        Assert.Equal("Café", DiscInfo.TryRead(blob)!.InternalName);
    }

    /// <summary>Verifies that a name without a NUL terminator reads the full 0x60-byte field.</summary>
    [Fact]
    public void GameCube_InternalNameWithoutNull_ReadsFullField()
    {
        var data = BuildDisc(wii: false, code: "GAL", country: 'E', maker: "01", regionWord: 1);
        for (var i = 0; i < 0x60; i++)
        {
            data[0x20 + i] = (byte)'X';
        }

        using var blob = PlainBlob.Open(new MemoryStream(data));
        Assert.Equal(new string('X', 0x60), DiscInfo.TryRead(blob)!.InternalName);
    }

    /// <summary>Verifies Dolphin's country-code mapping for GameCube discs.</summary>
    /// <param name="country">The country byte in the game ID.</param>
    /// <param name="regionWord">The region word written at 0x458.</param>
    /// <param name="revision">The disc revision byte.</param>
    /// <param name="expected">The expected country name.</param>
    [Theory]
    [InlineData('A', 99, 0, "World")]
    [InlineData('B', 1, 0, "USA")]
    [InlineData('N', 1, 0, "USA")]
    [InlineData('D', 2, 0, "Germany")]
    [InlineData('F', 2, 0, "France")]
    [InlineData('H', 2, 0, "Netherlands")]
    [InlineData('I', 2, 0, "Italy")]
    [InlineData('L', 2, 0, "Europe")]
    [InlineData('M', 2, 0, "Europe")]
    [InlineData('P', 2, 0, "Europe")]
    [InlineData('R', 2, 0, "Russia")]
    [InlineData('S', 2, 0, "Spain")]
    [InlineData('U', 2, 0, "Australia")]
    [InlineData('V', 2, 0, "Europe")]
    [InlineData('X', 1, 0, "USA")]
    [InlineData('Y', 1, 0, "USA")]
    [InlineData('Z', 1, 0, "USA")]
    [InlineData('J', 0, 0, "Japan")]
    [InlineData('K', 0, 0, "Korea")]
    [InlineData('Q', 0, 0, "Korea")]
    [InlineData('T', 0, 0, "Korea")]
    [InlineData('W', 0, 0, "Korea")]
    [InlineData('E', 1, 0, "USA")]
    [InlineData('E', 0, 0x30, "Korea")]
    [InlineData('Z', 2, 0, "Europe")]
    public void GameCube_CountryCode_MapsToCountry(char country, uint regionWord, byte revision,
        string expected)
    {
        var data = BuildDisc(wii: false, code: "GAL", country: country, maker: "01",
            revision: revision, regionWord: regionWord);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        Assert.Equal(expected, DiscInfo.TryRead(blob)!.Country);
    }

    /// <summary>Verifies that a country byte contradicting the region falls back to the region's country.</summary>
    [Fact]
    public void GameCube_ContradictingCountry_FallsBackToRegionCountry()
    {
        // Germany ('D') on an NTSC-U disc: Dolphin reports the region's typical country.
        var data = BuildDisc(wii: false, code: "GAL", country: 'D', maker: "01", regionWord: 1);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        var info = DiscInfo.TryRead(blob)!;
        Assert.Equal("NTSC-U", info.Region);
        Assert.Equal("USA", info.Country);
    }

    /// <summary>Verifies the region-word mapping for GameCube discs.</summary>
    /// <param name="regionWord">The region word written at 0x458.</param>
    /// <param name="expected">The expected region name.</param>
    [Theory]
    [InlineData(0u, "NTSC-J")]
    [InlineData(1u, "NTSC-U")]
    [InlineData(2u, "PAL")]
    [InlineData(4u, "NTSC-K")]
    [InlineData(3u, "Unknown")]
    [InlineData(99u, "Unknown")]
    public void GameCube_RegionWord_MapsToRegion(uint regionWord, string expected)
    {
        var data = BuildDisc(wii: false, code: "GAL", country: 'A', maker: "01", regionWord: regionWord);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        Assert.Equal(expected, DiscInfo.TryRead(blob)!.Region);
    }

    /// <summary>Verifies that an unknown country byte and region yield "Unknown".</summary>
    [Fact]
    public void GameCube_UnknownCountryAndRegion_ReportsUnknown()
    {
        var data = BuildDisc(wii: false, code: "GAL", country: '2', maker: "01", regionWord: 99);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        Assert.Equal("Unknown", DiscInfo.TryRead(blob)!.Country);
    }

    /// <summary>Verifies the Wii region word at 0x4E000 and the Wii-specific country codes.</summary>
    /// <param name="country">The country byte in the game ID.</param>
    /// <param name="regionWord">The region word written at 0x4E000.</param>
    /// <param name="expectedRegion">The expected region name.</param>
    /// <param name="expectedCountry">The expected country name.</param>
    [Theory]
    [InlineData('A', 99u, "Unknown", "World")]
    [InlineData('K', 4u, "NTSC-K", "Korea")]
    [InlineData('W', 2u, "PAL", "Europe")]
    [InlineData('W', 0u, "NTSC-J", "Taiwan")]
    [InlineData('E', 1u, "NTSC-U", "USA")]
    public void Wii_RegionAndCountry(char country, uint regionWord, string expectedRegion,
        string expectedCountry)
    {
        var data = BuildDisc(wii: true, code: "SAL", country: country, maker: "01",
            regionWord: regionWord);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        var info = DiscInfo.TryRead(blob)!;
        Assert.Equal(DiscType.Wii, info.DiscType);
        Assert.Equal(expectedRegion, info.Region);
        Assert.Equal(expectedCountry, info.Country);
    }

    /// <summary>Verifies that the Wii title ID is read from the game partition's ticket.</summary>
    [Fact]
    public void Wii_ReadsTitleIdFromGamePartitionTicket()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 8, TestWiiIsoBuilder.RandomData(8));
        var expected = ReadBe64(iso, TestWiiIsoBuilder.PartitionOffset + 0x1DC);

        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var info = DiscInfo.TryRead(blob);

        Assert.NotNull(info);
        Assert.Equal(DiscType.Wii, info.DiscType);
        Assert.Equal(expected, info.TitleId);
    }

    /// <summary>Verifies that a Wii image without a game partition has no title ID.</summary>
    [Fact]
    public void Wii_NoGamePartition_NoTitleId()
    {
        var data = BuildDisc(wii: true, code: "SAL", country: 'E', maker: "01", regionWord: 1);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        var info = DiscInfo.TryRead(blob);
        Assert.NotNull(info);
        Assert.Null(info.TitleId);
    }

    private static byte[] BuildDisc(bool wii, string code, char country, string maker,
        byte revision = 0, string name = "", uint regionWord = 0, int size = 0x50000)
    {
        var data = new byte[size];
        var gameId = Encoding.ASCII.GetBytes(code + country + maker);
        gameId.CopyTo(data, 0);
        data[7] = revision;
        Encoding.ASCII.GetBytes(name).CopyTo(data, 0x20);
        if (wii)
        {
            WriteBe32(data, 0x18, WiiVolume.WII_MAGIC);
            WriteBe32(data, 0x4E000, regionWord);
        }
        else
        {
            WriteBe32(data, 0x1C, WiiVolume.GC_MAGIC);
            WriteBe32(data, 0x458, regionWord);
        }

        return data;
    }

    private static ulong ReadBe64(byte[] data, int offset)
    {
        return ((ulong)ReadBe32(data, offset) << 32) | ReadBe32(data, offset + 4);
    }

    private static uint ReadBe32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8)
                      | data[offset + 3]);
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
