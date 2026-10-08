using System.Buffers.Binary;
using RVZSharp.Blobs;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// End-to-end <see cref="TgcWriter"/> tests: convert a synthetic GameCube ISO to TGC and decode
/// it back through <see cref="TgcBlob"/>, plus header and option-validation tests. The decoded
/// image must be byte-identical to the input.
/// </summary>
public class TgcWriterTests
{
    [Fact]
    public void GameCubeIso_RoundTrips()
    {
        var iso = BuildGcIso();
        var tgc = Convert(iso);
        Assert.Equal(iso, Decode(tgc));
    }

    [Fact]
    public void RandomAccess_AcrossPatchedRegions_MatchesTheIso()
    {
        var iso = BuildGcIso();
        var tgc = Convert(iso);

        using var ms = new MemoryStream(tgc);
        using var blob = TgcBlob.Open(ms, leaveOpen: true);
        foreach (var (start, length) in new[] { (0, 0x200), (0x410, 0x40), (0x2F0, 0x40), (0x330, 0x30) })
        {
            var probe = new byte[length];
            Assert.Equal(length, blob.ReadAt(start, probe));
            Assert.Equal(iso.AsSpan(start, length).ToArray(), probe);
        }
    }

    [Fact]
    public void Header_HasExpectedFields()
    {
        var iso = BuildGcIso();
        var tgc = Convert(iso);

        Assert.Equal(0xA2380FAEu, BinaryPrimitives.ReadUInt32LittleEndian(tgc.AsSpan(0)));
        Assert.Equal((uint)TgcWriter.HeaderSize, ReadBe32(tgc, 8));
        Assert.Equal(0x80u, ReadBe32(tgc, 12));
        Assert.Equal(0x300u + TgcWriter.HeaderSize, ReadBe32(tgc, 16)); // fst_real
        Assert.Equal(0x30u, ReadBe32(tgc, 20)); // fst_size
        Assert.Equal(0x30u, ReadBe32(tgc, 24)); // fst_max_size
        Assert.Equal(0x800u + TgcWriter.HeaderSize, ReadBe32(tgc, 28)); // dol_real
        Assert.Equal(0x900u, ReadBe32(tgc, 32)); // dol_size (0x800 offset + 0x100 size)
        Assert.Equal(0x330u + TgcWriter.HeaderSize, ReadBe32(tgc, 36)); // file_area_real
        Assert.Equal(0x330u, ReadBe32(tgc, 52)); // file_area_virtual
        Assert.Equal(tgc.Length, TgcWriter.HeaderSize + iso.Length);
    }

    [Fact]
    public void NoDol_IsTolerated()
    {
        var iso = BuildGcIso(dolOffset: 0);
        var tgc = Convert(iso);
        Assert.Equal(0u, ReadBe32(tgc, 32)); // dol_size
        Assert.Equal(iso, Decode(tgc));
    }

    [Fact]
    public void WiiDisc_IsRejected()
    {
        var iso = TestWiiIsoBuilder.Build(
            Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray(), 4,
            TestWiiIsoBuilder.RandomData(4));
        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => TgcWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void DiscWithoutMagic_IsRejected()
    {
        var iso = new byte[0x800];
        new Random(42).NextBytes(iso);
        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => TgcWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms));
        Assert.Equal(0, ms.Length);
    }

    private static byte[] Convert(byte[] iso)
    {
        using var ms = new MemoryStream();
        TgcWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms);
        return ms.ToArray();
    }

    private static byte[] Decode(byte[] tgc)
    {
        using var ms = new MemoryStream(tgc);
        using var blob = TgcBlob.Open(ms, leaveOpen: true);
        var iso = new byte[blob.Length];
        var total = 0;
        while (total < iso.Length)
        {
            var read = blob.ReadAt(total, iso.AsSpan(total));
            Assert.True(read > 0, $"read stopped at 0x{total:X}");
            total += read;
        }

        return iso;
    }

    /// <summary>
    /// A synthetic GameCube ISO with a plausible DOL (one text segment) and FST (root + one
    /// file entry), so the TGC header fields carry realistic values.
    /// </summary>
    private static byte[] BuildGcIso(uint dolOffset = 0x800)
    {
        var iso = new byte[0x4000];
        new Random(42).NextBytes(iso);

        iso[0x1C] = 0xC2; // GC DVD magic
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;
        WriteBe32(iso, 0x420, dolOffset);
        WriteBe32(iso, 0x424, 0x300); // FST offset
        WriteBe32(iso, 0x428, 0x30); // FST size
        WriteBe32(iso, 0x42C, 0x30); // FST max size

        // FST: root directory with two entries; one file at virtual offset 0x1000.
        iso[0x300] = 1;
        WriteBe32(iso, 0x308, 2);
        iso[0x30C] = 0;
        WriteBe32(iso, 0x310, 0x1000);
        WriteBe32(iso, 0x314, 0x100);

        if (dolOffset != 0)
        {
            // Real DOL headers zero the unused segment fields; do the same so the computed
            // DOL size only reflects the one text segment.
            Array.Clear(iso, (int)dolOffset, 0xE0);
            WriteBe32(iso, (int)dolOffset, 0x800); // text 0 offset
            WriteBe32(iso, (int)dolOffset + 0x90, 0x100); // text 0 size
        }

        return iso;
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static uint ReadBe32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
    }
}
