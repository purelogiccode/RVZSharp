using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class CisoBlobTests
{
    private const int BlockSize = 0x8000;

    [Fact]
    public void RoundTrip_PresentAndAbsentBlocks()
    {
        var iso = new byte[0x18000];
        new Random(31).NextBytes(iso);

        // Blocks 0 and 2 stored; block 1 (and everything after 0x18000) decodes to zeroes.
        var ciso = TestLegacyBuilders.BuildCiso(iso, BlockSize, [0, 2]);

        using var reader = CisoBlob.Open(new MemoryStream(ciso));
        Assert.Equal(BlobType.Ciso, reader.Type);
        Assert.Equal((long)CisoBlob.MapSize * BlockSize, reader.Length);

        var probe = new byte[0x18000];
        reader.ReadAt(0, probe);

        var expected = new byte[0x18000];
        iso.AsSpan(0, BlockSize).CopyTo(expected.AsSpan(0, BlockSize)); // block 0
        iso.AsSpan(2 * BlockSize).CopyTo(expected.AsSpan(2 * BlockSize)); // block 2
        Assert.Equal(expected, probe);
    }

    [Fact]
    public void AbsentBlock_ZeroFilled()
    {
        var iso = new byte[0x8000];
        new Random(32).NextBytes(iso);
        var ciso = TestLegacyBuilders.BuildCiso(iso, BlockSize, [1]); // block 0 absent

        using var reader = CisoBlob.Open(new MemoryStream(ciso));
        var probe = new byte[0x8000];
        reader.ReadAt(0, probe);
        Assert.Equal(new byte[0x8000], probe);
    }

    [Fact]
    public void PartialLastBlock_ZeroPadded()
    {
        // Only block 0 is present; block 1 (which holds the ISO's tail) is absent and
        // decodes to zeroes — the ISO content beyond the stored block is not preserved.
        var iso = new byte[0x9000];
        new Random(33).NextBytes(iso);
        var ciso = TestLegacyBuilders.BuildCiso(iso, BlockSize, [0]);

        using var reader = CisoBlob.Open(new MemoryStream(ciso));
        var probe = new byte[0x10000];
        reader.ReadAt(0, probe);

        var expected = new byte[0x10000];
        iso.AsSpan(0, BlockSize).CopyTo(expected.AsSpan(0, BlockSize));
        Assert.Equal(expected, probe);
    }

    [Fact]
    public void InvalidMapEntry_TreatedAsAbsent()
    {
        var iso = new byte[0x8000];
        new Random(34).NextBytes(iso);
        var ciso = TestLegacyBuilders.BuildCiso(iso, BlockSize, []);
        ciso[8 + 0] = 2; // invalid map value — Dolphin treats it as absent

        using var reader = CisoBlob.Open(new MemoryStream(ciso));
        var probe = new byte[0x8000];
        reader.ReadAt(0, probe);
        Assert.Equal(new byte[0x8000], probe);
    }

    [Fact]
    public void BadMagic_ThrowsFormatException()
    {
        var bytes = new byte[CisoBlob.HeaderSize];
        Assert.Throws<RvzFormatException>(() => CisoBlob.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void ZeroBlockSize_ThrowsFormatException()
    {
        var bytes = new byte[CisoBlob.HeaderSize];
        "CISO"u8.CopyTo(bytes);
        Assert.Throws<RvzFormatException>(() => CisoBlob.Open(new MemoryStream(bytes)));
    }
}
