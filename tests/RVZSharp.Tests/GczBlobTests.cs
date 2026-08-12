using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class GczBlobTests
{
    private static byte[] MakeIso(int length = 0x18000, int seed = 21)
    {
        var iso = new byte[length];
        new Random(seed).NextBytes(iso);
        return iso;
    }

    private static byte[] ReadAll(GczBlob reader)
    {
        var output = new byte[reader.Length];
        var position = 0;
        while (position < output.Length)
        {
            var read = reader.ReadAt(position, output.AsSpan(position));
            Assert.True(read > 0);
            position += read;
        }

        return output;
    }

    [Fact]
    public void RoundTrip_AllCompressed()
    {
        var iso = MakeIso();
        var gcz = TestLegacyBuilders.BuildGcz(iso);

        using var reader = GczBlob.Open(new MemoryStream(gcz));
        Assert.Equal(BlobType.Gcz, reader.Type);
        Assert.Equal(iso.Length, reader.Length);
        Assert.Equal(iso, ReadAll(reader));
    }

    [Fact]
    public void RoundTrip_MixedRawAndCompressed()
    {
        // Random data does not compress, so these blocks are stored raw; the deterministic
        // prefix compresses. The odd last block is zero-padded and stored.
        var iso = MakeIso();
        var prefix = Enumerable.Range(0, 0x2000).Select(i => (byte)(i % 251)).ToArray();
        prefix.CopyTo(iso, 0x8000);

        var gcz = TestLegacyBuilders.BuildGcz(iso, blockSize: 0x4000, compress: true);
        using var reader = GczBlob.Open(new MemoryStream(gcz));
        Assert.Equal(iso, ReadAll(reader));
    }

    [Fact]
    public void UnalignedDiscSize_ServesExactLength()
    {
        var iso = MakeIso(0x10001); // not a multiple of the block size
        var gcz = TestLegacyBuilders.BuildGcz(iso, blockSize: 0x4000);

        using var reader = GczBlob.Open(new MemoryStream(gcz));
        Assert.Equal(iso.Length, reader.Length);
        Assert.Equal(iso, ReadAll(reader));
    }

    [Fact]
    public void RandomAccess_AcrossBlocks()
    {
        var iso = MakeIso(0x10000, seed: 22);
        var gcz = TestLegacyBuilders.BuildGcz(iso, blockSize: 0x4000);

        using var reader = GczBlob.Open(new MemoryStream(gcz));
        var probe = new byte[0x6000];
        var rng = new Random(23);
        for (var i = 0; i < 10; i++)
        {
            var offset = rng.Next(0, iso.Length - probe.Length);
            reader.ReadAt(offset, probe);
            Assert.Equal(iso.AsSpan(offset, probe.Length).ToArray(), probe);
        }
    }

    [Fact]
    public void CorruptBlock_ThrowsHashMismatch()
    {
        var iso = MakeIso(0x10000);
        var gcz = TestLegacyBuilders.BuildGcz(iso);
        gcz[^100] ^= 0x40; // flip a byte inside the data area

        using var reader = GczBlob.Open(new MemoryStream(gcz));
        Assert.Throws<RvzHashMismatchException>(() => ReadAll(reader));
    }

    [Fact]
    public void TruncatedData_ThrowsFormatException()
    {
        var iso = MakeIso(0x10000);
        var gcz = TestLegacyBuilders.BuildGcz(iso);

        Assert.Throws<RvzFormatException>(() =>
            GczBlob.Open(new MemoryStream(gcz.AsSpan(0, gcz.Length - 0x1000).ToArray())));
    }

    [Fact]
    public void BadMagic_ThrowsFormatException()
    {
        var bytes = new byte[64];
        "NOTG"u8.CopyTo(bytes);
        Assert.Throws<RvzFormatException>(() => GczBlob.Open(new MemoryStream(bytes)));
    }
}
