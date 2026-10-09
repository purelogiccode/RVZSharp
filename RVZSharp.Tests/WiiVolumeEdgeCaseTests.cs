using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

/// <summary>
/// Edge-case tests for <see cref="WiiVolume"/>: offset-shift detection, boot DOL/FST header
/// reads, disc-header flags, hostile partition tables, ticket title-key decryption (retail,
/// Korean, RVT and fallback common keys) and partition offset arithmetic.
/// </summary>
public class WiiVolumeEdgeCaseTests
{
    /// <summary>Verifies that the offset shift is 0 for GameCube, 2 for Wii and null without magic.</summary>
    [Fact]
    public void TryGetOffsetShift_DetectsDiscType()
    {
        using (var gc = PlainBlob.Open(new MemoryStream(BuildHeader(wii: false))))
        {
            Assert.Equal(0, WiiVolume.TryGetOffsetShift(gc));
        }

        using (var wii = PlainBlob.Open(new MemoryStream(BuildHeader(wii: true))))
        {
            Assert.Equal(2, WiiVolume.TryGetOffsetShift(wii));
        }

        using (var none = PlainBlob.Open(new MemoryStream(new byte[0x100])))
        {
            Assert.Null(WiiVolume.TryGetOffsetShift(none));
        }
    }

    /// <summary>Verifies that the boot DOL offset is unshifted on GameCube and shifted on Wii.</summary>
    [Fact]
    public void GetBootDolOffset_UsesDiscTypeShift()
    {
        var gc = BuildHeader(wii: false);
        WriteBe32(gc, 0x420, 0x800);
        using (var blob = PlainBlob.Open(new MemoryStream(gc)))
        {
            Assert.Equal(0x800UL, WiiVolume.GetBootDolOffset(blob));
        }

        var wii = BuildHeader(wii: true);
        WriteBe32(wii, 0x420, 0x800 >> 2);
        using (var blob2 = PlainBlob.Open(new MemoryStream(wii)))
        {
            Assert.Equal(0x800UL, WiiVolume.GetBootDolOffset(blob2));
        }
    }

    /// <summary>Verifies that a zero DOL offset and a missing magic both return null.</summary>
    [Fact]
    public void GetBootDolOffset_ZeroOrNoMagic_ReturnsNull()
    {
        var gc = BuildHeader(wii: false);
        using (var blob = PlainBlob.Open(new MemoryStream(gc)))
        {
            Assert.Null(WiiVolume.GetBootDolOffset(blob));
        }

        var random = new byte[0x100];
        using (var blob2 = PlainBlob.Open(new MemoryStream(random)))
        {
            Assert.Null(WiiVolume.GetBootDolOffset(blob2));
        }
    }

    /// <summary>Verifies that the boot DOL size is the largest end offset of the DOL segments.</summary>
    [Fact]
    public void GetBootDolSize_SumsSegments()
    {
        var data = new byte[0x1000];
        const uint dolOffset = 0x100;
        // text 0: offset 0x200, size 0x50 -> ends at 0x250 (the largest).
        WriteBe32(data, (int)dolOffset + 0x00, 0x200);
        WriteBe32(data, (int)dolOffset + 0x90, 0x50);
        // data 0: offset 0x100, size 0x30 -> ends at 0x130.
        WriteBe32(data, (int)dolOffset + 0x1C, 0x100);
        WriteBe32(data, (int)dolOffset + 0xAC, 0x30);

        using var blob = PlainBlob.Open(new MemoryStream(data));
        Assert.Equal(0x250u, WiiVolume.GetBootDolSize(blob, dolOffset));
    }

    /// <summary>Verifies that the boot DOL size returns null when the header cannot be read.</summary>
    [Fact]
    public void GetBootDolSize_Truncated_ReturnsNull()
    {
        using var blob = PlainBlob.Open(new MemoryStream(new byte[0x10]));
        Assert.Null(WiiVolume.GetBootDolSize(blob, 0));
    }

    /// <summary>Verifies the disc type detected from the header magic.</summary>
    [Fact]
    public void GetDiscType_DetectsMagic()
    {
        using (var gc = PlainBlob.Open(new MemoryStream(BuildHeader(wii: false))))
        {
            Assert.Equal(DiscType.GameCube, WiiVolume.GetDiscType(gc));
        }

        using (var wii = PlainBlob.Open(new MemoryStream(BuildHeader(wii: true))))
        {
            Assert.Equal(DiscType.Wii, WiiVolume.GetDiscType(wii));
        }

        using (var none = PlainBlob.Open(new MemoryStream(new byte[0x100])))
        {
            Assert.Equal(DiscType.Unknown, WiiVolume.GetDiscType(none));
        }
    }

    /// <summary>Verifies the Wii hash/encryption flags read from disc header 0x60/0x61.</summary>
    [Fact]
    public void HasWiiHashesAndEncryption_ReflectHeaderFlags()
    {
        var data = BuildHeader(wii: true);
        data[0x60] = 0;
        data[0x61] = 0;
        using (var blob = PlainBlob.Open(new MemoryStream(data)))
        {
            Assert.True(WiiVolume.HasWiiHashes(blob));
            Assert.True(WiiVolume.HasWiiEncryption(blob));
        }

        data[0x60] = 1;
        data[0x61] = 1;
        using (var blob2 = PlainBlob.Open(new MemoryStream(data)))
        {
            Assert.False(WiiVolume.HasWiiHashes(blob2));
            Assert.False(WiiVolume.HasWiiEncryption(blob2));
        }
    }

    /// <summary>Verifies that IsWiiDisc is true only for the Wii magic.</summary>
    [Fact]
    public void IsWiiDisc_OnlyForWiiMagic()
    {
        using (var wii = PlainBlob.Open(new MemoryStream(BuildHeader(wii: true))))
        {
            Assert.True(WiiVolume.IsWiiDisc(wii));
        }

        using (var gc = PlainBlob.Open(new MemoryStream(BuildHeader(wii: false))))
        {
            Assert.False(WiiVolume.IsWiiDisc(gc));
        }
    }

    /// <summary>Verifies that a hostile partition-table count with a readable table throws.</summary>
    [Fact]
    public void GetPartitions_HostileCount_Throws()
    {
        var data = BuildHeader(wii: true, size: 0x50000);
        WriteBe32(data, 0x40000, 5000); // > maxEntriesPerGroup
        WriteBe32(data, 0x40004, 0x40020 >> 2);
        using var blob = PlainBlob.Open(new MemoryStream(data));

        Assert.Throws<RvzFormatException>(() => WiiVolume.GetPartitions(blob));
    }

    /// <summary>Verifies that a hostile count with an unreadable table is skipped.</summary>
    [Fact]
    public void GetPartitions_HostileCountBadOffset_SkipsGroup()
    {
        var data = BuildHeader(wii: true, size: 0x50000);
        WriteBe32(data, 0x40000, 5000);
        WriteBe32(data, 0x40004, 0x7FFFFFFF); // shifted far past the file
        using var blob = PlainBlob.Open(new MemoryStream(data));

        Assert.Empty(WiiVolume.GetPartitions(blob));
    }

    /// <summary>Verifies that a partition with an invalid ticket signature is skipped.</summary>
    [Fact]
    public void GetPartitions_InvalidTicket_Skipped()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 4, TestWiiIsoBuilder.RandomData(4));
        WriteBe32(iso, TestWiiIsoBuilder.PartitionOffset, 0); // invalid signature type
        using var blob = PlainBlob.Open(new MemoryStream(iso));

        Assert.Empty(WiiVolume.GetPartitions(blob));
    }

    /// <summary>Verifies that a short image without a partition table yields no partitions.</summary>
    [Fact]
    public void GetPartitions_ShortImage_Empty()
    {
        using var blob = PlainBlob.Open(new MemoryStream(BuildHeader(wii: true, size: 0x1000)));
        Assert.Empty(WiiVolume.GetPartitions(blob));
    }

    /// <summary>Verifies that get title key rejects a ticket shorter than the key fields.</summary>
    [Fact]
    public void GetTitleKey_ShortTicket_Throws()
    {
        Assert.Throws<ArgumentException>(() => WiiVolume.GetTitleKey(new byte[0x100]));
    }

    /// <summary>Verifies that a Korean ticket (common-key index 1) uses the Korean common key.</summary>
    [Fact]
    public void GetTitleKey_KoreanIndex_UsesKoreanKey()
    {
        var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 3)).ToArray();
        var ticket = BuildTicket(titleKey, KoreanCommonKey);
        ticket[0x1F1] = 1;

        Assert.Equal(titleKey, WiiVolume.GetTitleKey(ticket));
    }

    /// <summary>Verifies that an RVT/iQue ticket (issuer Root-CA00000002-XS00000006) uses the RVT key.</summary>
    [Fact]
    public void GetTitleKey_RvtIssuer_UsesRvtKey()
    {
        var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(i * 11 + 5)).ToArray();
        var ticket = BuildTicket(titleKey, RvtCommonKey);
        "Root-CA00000002-XS00000006"u8.CopyTo(ticket.AsSpan(0x140));

        Assert.Equal(titleKey, WiiVolume.GetTitleKey(ticket));
    }

    /// <summary>Verifies that a common-key index other than 0 or 1 falls back to the retail key.</summary>
    [Fact]
    public void GetTitleKey_UnknownIndex_FallsBackToRetailKey()
    {
        var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(i * 13 + 9)).ToArray();
        var ticket = BuildTicket(titleKey, RetailCommonKey);
        ticket[0x1F1] = 2;

        Assert.Equal(titleKey, WiiVolume.GetTitleKey(ticket));
    }

    /// <summary>Verifies that a Korean ticket decrypted with the retail key does not match.</summary>
    [Fact]
    public void GetTitleKey_WrongCommonKey_DoesNotMatch()
    {
        var titleKey = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 3)).ToArray();
        var ticket = BuildTicket(titleKey, KoreanCommonKey); // index 0 => retail key

        Assert.NotEqual(titleKey, WiiVolume.GetTitleKey(ticket));
    }

    /// <summary>Verifies that a partition-data offset maps to a raw disc offset.</summary>
    [Fact]
    public void PartitionOffsetToRawOffset_AddsPartitionAndDataOffsets()
    {
        var partition = new Partition
        {
            Offset = 0x100000,
            Type = 0,
            DataOffset = 0x40000,
            DataSize = 0x1000,
            Key = new byte[16]
        };

        Assert.Equal(0x140100UL, WiiVolume.PartitionOffsetToRawOffset(0x100, partition));
    }

    /// <summary>Verifies the big-endian and shifted header reads, including their null cases.</summary>
    [Fact]
    public void ReadSwapped_ReadsHeaderValues()
    {
        var gc = BuildHeader(wii: false);
        WriteBe32(gc, 0x420, 0x1234);
        using var blob = PlainBlob.Open(new MemoryStream(gc));

        Assert.Equal(0x1234u, WiiVolume.ReadSwapped(blob, 0x420));
        Assert.Equal(0x1234UL << 2, WiiVolume.ReadSwappedAndShifted(blob, 0x420)); // Wii field: << 2
        Assert.Equal(0u, WiiVolume.ReadSwapped(blob, 0x999999)); // read failure => 0
        Assert.Null(WiiVolume.ReadSwappedAndShifted(blob, 0x999999));
    }

    /// <summary>Verifies the Wii partition FST offset/size reads from a decrypted partition view.</summary>
    [Fact]
    public void GetFstOffsetAndSize_FromPartitionHeader()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var data = TestWiiIsoBuilder.RandomData(2);
        WriteBe32(data, 0x18, WiiVolume.WII_MAGIC);
        WriteBe32(data, 0x424, 0x3000 >> 2);
        WriteBe32(data, 0x428, 0x120 >> 2);
        var iso = TestWiiIsoBuilder.Build(key, 2, data);
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var partition = WiiVolume.GetPartitions(blob)[0];

        Assert.Equal(0x3000UL, WiiVolume.GetFstOffset(blob, partition));
        Assert.Equal(0x120UL, WiiVolume.GetFstSize(blob, partition));
    }

    private static byte[] BuildHeader(bool wii, int size = 0x1000)
    {
        var data = new byte[size];
        WriteBe32(data, wii ? 0x18 : 0x1C, wii ? WiiVolume.WII_MAGIC : WiiVolume.GC_MAGIC);
        return data;
    }

    /// <summary>Builds a 0x2A4-byte ticket whose title key is encrypted with <paramref name="commonKey"/>.</summary>
    private static byte[] BuildTicket(byte[] titleKey, byte[] commonKey)
    {
        var ticket = new byte[0x2A4];
        ticket[0] = 0x00;
        ticket[1] = 0x01;
        ticket[2] = 0x00;
        ticket[3] = 0x01; // RSA2048 signature type (0x10001 big endian)
        var iv = new byte[16];
        ticket.AsSpan(0x1DC, 8).CopyTo(iv);
        using var aes = Aes.Create();
        aes.Key = commonKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(commonKey, iv);
        var encrypted = encryptor.TransformFinalBlock(titleKey, 0, 16);
        encrypted.CopyTo(ticket, 0x1BF);
        return ticket;
    }

    // Dolphin IOSC common keys (the same constants the library selects between).
    private static readonly byte[] RetailCommonKey =
    [
        0xEB, 0xE4, 0x2A, 0x22, 0x5E, 0x85, 0x93, 0xE4,
        0x48, 0xD9, 0xC5, 0x45, 0x73, 0x81, 0xAA, 0xF7
    ];

    private static readonly byte[] KoreanCommonKey =
    [
        0x63, 0xB8, 0x2B, 0xB4, 0xF4, 0x61, 0x4E, 0x2E,
        0x13, 0xF2, 0xFE, 0xFB, 0xBA, 0x4C, 0x9B, 0x7E
    ];

    private static readonly byte[] RvtCommonKey =
    [
        0xA1, 0x60, 0x4A, 0x6A, 0x71, 0x23, 0xB5, 0x29,
        0xAE, 0x8B, 0xEC, 0x32, 0xC8, 0x16, 0xFC, 0xAA
    ];

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
