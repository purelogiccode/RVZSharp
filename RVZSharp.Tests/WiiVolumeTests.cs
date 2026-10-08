using RVZSharp.Blobs;
using RVZSharp.Tests.Helpers;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for <see cref="WiiVolume"/> ticket handling: retail tickets store the title key
/// AES-CBC encrypted with the console's common key (IV = title ID), and partition discovery
/// must return the plaintext key (from an RVZ/WIA container when available).
/// </summary>
public class WiiVolumeTests
{
    /// <summary>Verifies that get title key decrypts retail ticket.</summary>
    [Fact]
    public void GetTitleKey_DecryptsRetailTicket()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 5 + 2)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 8, TestWiiIsoBuilder.RandomData(8));

        var ticket = iso.AsSpan(TestWiiIsoBuilder.PartitionOffset, 0x2A4).ToArray();
        Assert.Equal(key, WiiVolume.GetTitleKey(ticket));
    }

    /// <summary>Verifies that get partitions plain ISO returns plaintext key.</summary>
    [Fact]
    public void GetPartitions_PlainIso_ReturnsPlaintextKey()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 5 + 2)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 8, TestWiiIsoBuilder.RandomData(8));

        using var blob = PlainBlob.Open(new MemoryStream(iso));
        var partition = Assert.Single(WiiVolume.GetPartitions(blob));
        Assert.Equal(key, partition.Key);
    }
}
