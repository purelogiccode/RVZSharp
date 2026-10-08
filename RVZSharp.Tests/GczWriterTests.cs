using System.Buffers.Binary;
using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// End-to-end <see cref="GczWriter"/> tests: convert a synthetic disc image (GameCube or Wii)
/// to GCZ and decode it back byte-exactly through <see cref="GczBlob"/> (which also validates
/// every block's Adler-32), plus header, block-storage and option-validation tests.
/// </summary>
public class GczWriterTests
{
    private const ulong UncompressedFlag = 1UL << 63;

    /// <summary>Verifies that game cube ISO round trips.</summary>
    [Fact]
    public void GameCubeIso_RoundTrips()
    {
        var iso = BuildGcIso(0x200000 * 2 + 0x20000);
        var gcz = Convert(iso);
        Assert.Equal(iso, Decode(gcz));
    }

    /// <summary>Verifies that Wii ISO round trips.</summary>
    [Fact]
    public void WiiIso_RoundTrips()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var gcz = Convert(iso);
        Assert.Equal(iso, Decode(gcz));
    }

    /// <summary>Verifies that last partial block is zero padded and round trips.</summary>
    [Fact]
    public void LastPartialBlock_IsZeroPaddedAndRoundTrips()
    {
        // 0x12345 is not a multiple of the block size: the last block is zero-padded.
        var iso = BuildGcIso(0x200000 + 0x12345);
        var gcz = Convert(iso, blockSize: 0x4000);

        var numBlocks = (iso.Length + 0x3FFF) / 0x4000;
        Assert.Equal((uint)numBlocks, ReadLe32(gcz, 28));
        Assert.Equal(iso, Decode(gcz));
    }

    /// <summary>Verifies that header has expected fields.</summary>
    [Fact]
    public void Header_HasExpectedFields()
    {
        var iso = BuildGcIso(0x200000 + 0x12345);
        var gcz = Convert(iso, blockSize: 0x4000);

        var numBlocks = (iso.Length + 0x3FFF) / 0x4000;
        Assert.Equal(0xB10BC001u, ReadLe32(gcz, 0));
        Assert.Equal(0u, ReadLe32(gcz, 4)); // sub_type: 0 = GameCube
        Assert.Equal((ulong)iso.Length, ReadLe64(gcz, 16));
        Assert.Equal(0x4000u, ReadLe32(gcz, 24));
        Assert.Equal((uint)numBlocks, ReadLe32(gcz, 28));

        var compressedDataSize = ReadLe64(gcz, 8);
        Assert.Equal(32 + 12L * numBlocks + (long)compressedDataSize, gcz.Length);
    }

    /// <summary>Verifies that Wii ISO header sub type is one.</summary>
    [Fact]
    public void WiiIso_HeaderSubTypeIsOne()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var iso = TestWiiIsoBuilder.Build(key, 130, TestWiiIsoBuilder.RandomData(130),
            corruptSomeHashes: true);
        var gcz = Convert(iso);
        Assert.Equal(1u, ReadLe32(gcz, 4)); // sub_type: 1 = Wii
    }

    /// <summary>Verifies that incompressible data stores raw blocks.</summary>
    [Fact]
    public void IncompressibleData_StoresRawBlocks()
    {
        var iso = new byte[0x4000 * 8];
        new Random(1).NextBytes(iso);
        SetGcMagic(iso);

        var gcz = Convert(iso);
        for (var i = 0; i < 8; i++)
        {
            var pointer = ReadLe64(gcz, 32 + i * 8);
            Assert.True((pointer & UncompressedFlag) != 0, $"block {i} should be stored raw");
        }

        Assert.Equal(iso, Decode(gcz));
    }

    /// <summary>Verifies that compressible data stores compressed blocks.</summary>
    [Fact]
    public void CompressibleData_StoresCompressedBlocks()
    {
        var iso = new byte[0x4000 * 8];
        SetGcMagic(iso);

        var gcz = Convert(iso);
        for (var i = 0; i < 8; i++)
        {
            var pointer = ReadLe64(gcz, 32 + i * 8);
            Assert.True((pointer & UncompressedFlag) == 0, $"block {i} should be compressed");
        }

        Assert.True(ReadLe64(gcz, 8) < 8 * 256, "zero blocks should compress to a few bytes each");
        Assert.Equal(iso, Decode(gcz));
    }

    /// <summary>Verifies that max threads output is identical to sequential.</summary>
    [Fact]
    public void MaxThreads_OutputIsIdenticalToSequential()
    {
        // Parallel compression must be byte-identical to sequential: blocks are written in
        // order (1062 blocks span many batches with four threads).
        var iso = BuildGcIso(0x200000 + 0x12345);
        var sequential = Convert(iso, blockSize: 0x800, maxThreads: 1);
        var parallel = Convert(iso, blockSize: 0x800, maxThreads: 4);
        Assert.Equal(sequential, parallel);
    }

    /// <summary>Verifies that non power of two block size is rejected.</summary>
    [Fact]
    public void NonPowerOfTwoBlockSize_IsRejected()
    {
        var iso = BuildGcIso(0x200000);
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => GczWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new GczWriteOptions { BlockSize = 0x3000 }));
        Assert.Equal(0, ms.Length);
    }

    /// <summary>Verifies that negative max threads is rejected.</summary>
    [Fact]
    public void NegativeMaxThreads_IsRejected()
    {
        var iso = BuildGcIso(0x200000);
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => GczWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms, new GczWriteOptions { MaxThreads = -1 }));
        Assert.Equal(0, ms.Length);
    }

    /// <summary>Verifies that non seekable output is rejected.</summary>
    [Fact]
    public void NonSeekableOutput_IsRejected()
    {
        var iso = BuildGcIso(0x200000);
        using var output = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() => GczWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), output));
        Assert.Equal(0, output.Length);
    }

    /// <summary>Verifies that disc without magic is rejected.</summary>
    [Fact]
    public void DiscWithoutMagic_IsRejected()
    {
        var iso = new byte[0x200000];
        new Random(42).NextBytes(iso);

        using var ms = new MemoryStream();
        Assert.Throws<RvzFormatException>(() => GczWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), ms));
        Assert.Equal(0, ms.Length);
    }

    private static byte[] Convert(byte[] iso, int blockSize = 0x4000, int maxThreads = 0)
    {
        using var ms = new MemoryStream();
        GczWriter.Write(PlainBlob.Open(new MemoryStream(iso)), ms, new GczWriteOptions
        {
            BlockSize = blockSize,
            MaxThreads = maxThreads
        });
        return ms.ToArray();
    }

    private static byte[] Decode(byte[] gcz)
    {
        using var ms = new MemoryStream(gcz);
        using var blob = GczBlob.Open(ms, leaveOpen: true);
        var iso = new byte[blob.Length];
        var total = 0;
        while (total < iso.Length)
        {
            var read = blob.ReadAt(total, iso.AsSpan(total));
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        Assert.Equal(iso.Length, total);
        return iso;
    }

    /// <summary>A GameCube-style ISO of the given length: random data with the GC disc magic.</summary>
    private static byte[] BuildGcIso(int length)
    {
        var iso = new byte[length];
        new Random(42).NextBytes(iso);
        SetGcMagic(iso);
        return iso;
    }

    private static void SetGcMagic(byte[] iso)
    {
        iso[0x1C] = 0xC2; // GC DVD magic (0xC2339F3D) so the writer treats it as a GC disc
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;
    }

    private static uint ReadLe32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    private static ulong ReadLe64(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
