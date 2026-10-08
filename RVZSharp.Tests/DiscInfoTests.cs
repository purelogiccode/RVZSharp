using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for <see cref="DiscInfo"/>: game ID, maker ID, revision, internal name, region,
/// country and title ID read from a decoded disc header.
/// </summary>
public class DiscInfoTests
{
    private static byte[] MakeGcIso()
    {
        var iso = new byte[0x420000];
        "GALE01"u8.CopyTo(iso);
        iso[7] = 2; // revision
        "Test Game"u8.CopyTo(iso.AsSpan(0x20));
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D; // GameCube DVD magic
        WriteBe32(iso, 0x458, 1); // NTSC-U
        return iso;
    }

    private static byte[] MakeWiiIso()
    {
        var key = new byte[16];
        new Random(3).NextBytes(key);
        var iso = TestWiiIsoBuilder.Build(key, sectorCount: 2, TestWiiIsoBuilder.RandomData(2));
        "SMNE01"u8.CopyTo(iso);
        iso[7] = 1;
        "Wii Test"u8.CopyTo(iso.AsSpan(0x20));
        iso[0x20 + 8] = 0; // terminate the internal name (the rest of the builder is random)
        WriteBe32(iso, 0x4E000, 1); // NTSC-U
        WriteBe64(iso, TestWiiIsoBuilder.PartitionOffset + 0x1DC, 0x00010000534D4E45); // title ID
        return iso;
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
        for (var i = 0; i < 8; i++)
        {
            data[offset + i] = (byte)(value >> (56 - 8 * i));
        }
    }

    /// <summary>Verifies that try read game cube reads metadata.</summary>
    [Fact]
    public void TryRead_GameCube_ReadsMetadata()
    {
        using var blob = PlainBlob.Open(new MemoryStream(MakeGcIso()));
        var info = DiscInfo.TryRead(blob);

        Assert.NotNull(info);
        Assert.Equal(DiscType.GameCube, info.DiscType);
        Assert.Equal("GALE01", info.GameId);
        Assert.Equal("01", info.MakerId);
        Assert.Equal(2, info.Revision);
        Assert.Equal("Test Game", info.InternalName);
        Assert.Equal("NTSC-U", info.Region);
        Assert.Equal("USA", info.Country);
        Assert.Equal('E', info.CountryCode);
        Assert.Null(info.TitleId);
    }

    /// <summary>Verifies that try read Wii reads title ID and region.</summary>
    [Fact]
    public void TryRead_Wii_ReadsTitleIdAndRegion()
    {
        using var blob = PlainBlob.Open(new MemoryStream(MakeWiiIso()));
        var info = DiscInfo.TryRead(blob);

        Assert.NotNull(info);
        Assert.Equal(DiscType.Wii, info.DiscType);
        Assert.Equal("SMNE01", info.GameId);
        Assert.Equal("Wii Test", info.InternalName);
        Assert.Equal("NTSC-U", info.Region);
        Assert.Equal("USA", info.Country);
        Assert.Equal(0x00010000534D4E45UL, info.TitleId);
    }

    /// <summary>Verifies that try read region fallback uses typical country when country byte contradicts region.</summary>
    [Fact]
    public void TryRead_RegionFallback_UsesTypicalCountry_WhenCountryByteContradictsRegion()
    {
        var iso = MakeGcIso();
        iso[3] = (byte)'J'; // country byte says Japan...
        WriteBe32(iso, 0x458, 1); // ...but the region word says NTSC-U

        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var info = DiscInfo.TryRead(blob);

        Assert.NotNull(info);
        Assert.Equal("NTSC-U", info.Region);
        Assert.Equal("USA", info.Country); // TypicalCountryForRegion fallback
    }

    /// <summary>Verifies that try read non disc returns null.</summary>
    [Fact]
    public void TryRead_NonDisc_ReturnsNull()
    {
        var garbage = new byte[0x420000];
        new Random(11).NextBytes(garbage);
        using var blob = PlainBlob.Open(new MemoryStream(garbage));

        Assert.Null(DiscInfo.TryRead(blob));
    }

    /// <summary>Verifies that try read too short returns null.</summary>
    [Fact]
    public void TryRead_TooShort_ReturnsNull()
    {
        using var blob = PlainBlob.Open(new MemoryStream(new byte[0x40]));

        Assert.Null(DiscInfo.TryRead(blob));
    }

    /// <summary>Verifies that read non disc throws format exception.</summary>
    [Fact]
    public void Read_NonDisc_ThrowsFormatException()
    {
        var garbage = new byte[0x420000];
        new Random(12).NextBytes(garbage);
        using var blob = PlainBlob.Open(new MemoryStream(garbage));

        Assert.Throws<RvzFormatException>(() => DiscInfo.Read(blob));
    }
}
