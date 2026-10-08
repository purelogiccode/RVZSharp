using System.Buffers.Binary;
using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// End-to-end <see cref="CisoWriter"/> tests: convert a synthetic disc image (GameCube or Wii)
/// to CISO and decode it back through <see cref="CisoBlob"/>, plus header, block-presence and
/// option-validation tests. The decoded image is always the map capacity
/// (<c>blockSize × 0x7FF8</c>), so tests compare the image prefix and probe the zero tail.
/// </summary>
public class CisoWriterTests
{
    [Fact]
    public void GameCubeIso_RoundTrips()
    {
        var iso = BuildGcIso(0x2C00);
        var ciso = Convert(iso);
        Assert.Equal(iso, DecodePrefix(ciso, iso.Length));
        AssertTailIsZero(ciso);
    }

    [Fact]
    public void WiiIso_RoundTrips()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var ciso = Convert(iso);
        Assert.Equal(iso, DecodePrefix(ciso, iso.Length));
        AssertTailIsZero(ciso);
    }

    [Fact]
    public void Header_HasMagicBlockSizeAndPresenceMap()
    {
        var iso = BuildGcIso(0x800);
        Array.Clear(iso, 0x400, 0x400); // block 1 is all zero: stored absent
        var ciso = Convert(iso, blockSize: 0x400);

        Assert.Equal("CISO"u8.ToArray(), ciso.AsSpan(0, 4).ToArray());
        Assert.Equal(0x400u, BinaryPrimitives.ReadUInt32LittleEndian(ciso.AsSpan(4)));
        Assert.Equal(1, ciso[8]); // block 0 is present
        Assert.Equal(0, ciso[9]); // block 1 is all zero: stored absent
        Assert.Equal(CisoBlob.HeaderSize + 0x400, ciso.Length);
    }

    [Fact]
    public void AllZeroBlocks_AreAbsent()
    {
        var iso = new byte[0x1000];
        SetGcMagic(iso);
        var ciso = Convert(iso, blockSize: 0x400);

        // Only the first block (with the magic) is stored.
        Assert.Equal(CisoBlob.HeaderSize + 0x400, ciso.Length);
        Assert.Equal(1, ciso[8]);
        Assert.Equal(0, ciso[9]);
        Assert.Equal(iso, DecodePrefix(ciso, iso.Length));
    }

    [Fact]
    public void Scrub_ZeroesNonGamePartitionData()
    {
        var iso = TestWiiIsoBuilder.BuildWithUpdatePartition();
        var ciso = Convert(iso, blockSize: 0x4000, scrub: true);
        var decoded = DecodePrefix(ciso, iso.Length);

        // The update partition data is zeroed; everything else is byte-identical.
        var dataStart = TestWiiIsoBuilder.UpdatePartitionOffset + TestWiiIsoBuilder.DataOffset;
        Assert.Equal(iso.AsSpan(0, dataStart).ToArray(), decoded.AsSpan(0, dataStart).ToArray());
        Assert.True(
            decoded.AsSpan(dataStart, TestWiiIsoBuilder.PartitionDataSize).IndexOfAnyExcept((byte)0) < 0,
            "the update partition data should be zeroed");
        var afterEnd = dataStart + TestWiiIsoBuilder.PartitionDataSize;
        Assert.Equal(iso.AsSpan(afterEnd).ToArray(), decoded.AsSpan(afterEnd).ToArray());
    }

    [Fact]
    public void Scrub_ShrinksTheFile()
    {
        var iso = TestWiiIsoBuilder.BuildWithUpdatePartition();
        var plain = Convert(iso, blockSize: 0x4000);
        var scrubbed = Convert(iso, blockSize: 0x4000, scrub: true);
        Assert.True(scrubbed.Length < plain.Length, "the scrubbed CISO should be smaller");
    }

    [Fact]
    public void ImageLargerThanMap_IsRejected()
    {
        var iso = new byte[CisoBlob.MapSize * 0x10 + 1];
        SetGcMagic(iso);
        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => CisoWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new CisoWriteOptions { BlockSize = 0x10 }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void NonPowerOfTwoBlockSize_IsRejected()
    {
        var iso = BuildGcIso(0x800);
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => CisoWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new CisoWriteOptions { BlockSize = 0x300 }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void NonSeekableOutput_IsRejected()
    {
        var iso = BuildGcIso(0x800);
        using var output = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() => CisoWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void DiscWithoutMagic_IsRejected()
    {
        var iso = new byte[0x800];
        new Random(42).NextBytes(iso);
        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => CisoWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms));
        Assert.Equal(0, ms.Length);
    }

    private static byte[] Convert(byte[] iso, int blockSize = 0x400, bool scrub = false)
    {
        using var ms = new MemoryStream();
        CisoWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new CisoWriteOptions
        {
            BlockSize = blockSize,
            Scrub = scrub
        });
        return ms.ToArray();
    }

    private static byte[] DecodePrefix(byte[] ciso, int length)
    {
        using var ms = new MemoryStream(ciso);
        using var blob = CisoBlob.Open(ms, leaveOpen: true);
        var output = new byte[length];
        var total = 0;
        while (total < output.Length)
        {
            var read = blob.ReadAt(total, output.AsSpan(total));
            Assert.True(read > 0, $"read stopped at 0x{total:X}");
            total += read;
        }

        return output;
    }

    /// <summary>Probes the end of the decoded capacity: absent blocks must decode to zeroes.</summary>
    private static void AssertTailIsZero(byte[] ciso)
    {
        using var ms = new MemoryStream(ciso);
        using var blob = CisoBlob.Open(ms, leaveOpen: true);
        var probe = new byte[0x100];
        Assert.Equal(probe.Length, blob.ReadAt(blob.Length - probe.Length, probe));
        Assert.All(probe, value => Assert.Equal(0, value));
    }

    private static byte[] BuildGcIso(int length)
    {
        var iso = new byte[length];
        new Random(42).NextBytes(iso);
        SetGcMagic(iso);
        return iso;
    }

    private static void SetGcMagic(byte[] iso)
    {
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
