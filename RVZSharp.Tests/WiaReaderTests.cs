using RVZSharp.Blobs;
using RVZSharp.Compression;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public class WiaReaderTests
{
    private static byte[] ReadAll(RvzReader reader)
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

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Purge)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Lzma2)]
    public void GameCubeDisc_RoundTrips(CompressionType compression)
    {
        var spec = new RvzSpec
        {
            IsWia = true,
            Compression = compression,
            ChunkSize = WiaDisc.GroupSize,
            RawSize = 3 * 0x8000 + 0x1234,
            RawTailSize = 0x9000,
            Seed = 4
        };
        var (file, iso) = TestRvzBuilder.BuildWithIso(spec);

        using var reader = RvzReader.OpenWia(new MemoryStream(file));
        Assert.True(reader.IsWia);
        Assert.Equal(BlobType.Wia, reader.Type);
        Assert.Equal(iso.Length, reader.Length);
        Assert.Equal(iso, ReadAll(reader));
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Purge)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Lzma2)]
    public void WiiDisc_WithExceptions_RoundTrips(CompressionType compression)
    {
        var exceptions = new[]
        {
            new[] { new HashExceptionEntry(0x100, Enumerable.Range(0, 20).Select(i => (byte)i).ToArray()) },
            Array.Empty<HashExceptionEntry>()
        };
        var spec = new RvzSpec
        {
            IsWia = true,
            Compression = compression,
            ChunkSize = WiaDisc.GroupSize,
            RawSize = 0x8000,
            Partition = new PartitionSpec { SectorCount = 130, Exceptions = exceptions },
            Seed = 5
        };
        var (file, iso) = TestRvzBuilder.BuildWithIso(spec);

        using var reader = RvzReader.OpenWia(new MemoryStream(file));
        Assert.Equal(iso, ReadAll(reader));
    }

    [Fact]
    public void RandomAccess_AcrossChunks()
    {
        var spec = new RvzSpec
        {
            IsWia = true,
            Compression = CompressionType.Bzip2,
            ChunkSize = WiaDisc.GroupSize,
            RawSize = 2 * 0x200000 + 0x8000,
            Partition = new PartitionSpec { SectorCount = 70 },
            Seed = 6
        };
        var (file, iso) = TestRvzBuilder.BuildWithIso(spec);

        using var reader = RvzReader.OpenWia(new MemoryStream(file));
        var probe = new byte[0x5000];
        var rng = new Random(9);
        for (var i = 0; i < 20; i++)
        {
            var offset = rng.Next(0, iso.Length - probe.Length);
            reader.ReadAt(offset, probe);
            Assert.Equal(iso.AsSpan(offset, probe.Length).ToArray(), probe);
        }
    }

    [Fact]
    public void Zstd_RejectedForWia()
    {
        var spec = new RvzSpec
        {
            IsWia = true,
            Compression = CompressionType.Zstd,
            ChunkSize = WiaDisc.GroupSize,
            RawSize = 0x8000
        };
        var file = TestRvzBuilder.Build(spec);

        Assert.Throws<RvzUnsupportedException>(() => RvzReader.OpenWia(new MemoryStream(file)));
    }

    [Fact]
    public void Purge_RejectedForRvz()
    {
        var spec = new RvzSpec
        {
            Compression = CompressionType.Purge,
            ChunkSize = WiaDisc.GroupSize,
            RawSize = 0x8000
        };
        var file = TestRvzBuilder.Build(spec);

        Assert.Throws<RvzUnsupportedException>(() => RvzReader.Open(new MemoryStream(file)));
    }

    [Fact]
    public void SmallChunkSize_RejectedForWia()
    {
        var spec = new RvzSpec
        {
            IsWia = true,
            Compression = CompressionType.None,
            ChunkSize = 0x8000, // RVZ-legal small chunk, not a multiple of 2 MiB
            RawSize = 0x8000
        };
        var file = TestRvzBuilder.Build(spec);

        Assert.Throws<RvzFormatException>(() => RvzReader.OpenWia(new MemoryStream(file)));
    }

    [Fact]
    public void BadMagic_Rejected()
    {
        var spec = new RvzSpec { Compression = CompressionType.None, RawSize = 0x8000 };
        var file = TestRvzBuilder.Build(spec); // RVZ magic

        // The WIA entry point rejects RVZ magic and vice versa.
        Assert.Throws<RvzFormatException>(() => RvzReader.OpenWia(new MemoryStream(file)));
        using var rvz = RvzReader.Open(new MemoryStream(file));
        Assert.Equal(BlobType.Rvz, rvz.Type);
    }

    [Fact]
    public void BlobFactory_DetectsWia()
    {
        var spec = new RvzSpec
        {
            IsWia = true,
            Compression = CompressionType.Bzip2,
            ChunkSize = WiaDisc.GroupSize,
            RawSize = 0x8000
        };
        var file = TestRvzBuilder.Build(spec);

        using var reader = Blob.Open(new MemoryStream(file));
        Assert.Equal(BlobType.Wia, reader.Type);
        Assert.IsType<RvzReader>(reader);
    }
}

public class PurgeDecoderTests
{
    [Fact]
    public void RoundTrip_SegmentsAndZeroFill()
    {
        // Data with long zero runs (which the encoder skips) and short ones (which it keeps).
        var data = new byte[0x10000];
        new Random(11).NextBytes(data.AsSpan(0x100, 0x200));
        new Random(12).NextBytes(data.AsSpan(0x3000, 0x50));
        new Random(13).NextBytes(data.AsSpan(0x7000, 0x20));

        var encoded = TestCompressor.CompressPurge(data);
        Assert.Equal(data, PurgeDecoder.Decode(encoded, [], data.Length));
    }

    [Fact]
    public void RoundTrip_WithPrecedingData()
    {
        var data = new byte[0x8000];
        new Random(14).NextBytes(data.AsSpan(0x10, 0x100));
        var preceding = new byte[] { 0x00, 0x02, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 0, 0 };

        var encoded = TestCompressor.CompressPurge(data, preceding);
        Assert.Equal(data, PurgeDecoder.Decode(encoded, preceding, data.Length));
    }

    [Fact]
    public void AllZeroData_ProducesEmptyStream()
    {
        var data = new byte[0x8000];
        var encoded = TestCompressor.CompressPurge(data);

        // Only the SHA-1 trailer remains.
        Assert.Equal(20, encoded.Length);
        Assert.Equal(data, PurgeDecoder.Decode(encoded, [], data.Length));
    }

    [Fact]
    public void TamperedTrailer_ThrowsHashMismatch()
    {
        var data = new byte[0x1000];
        new Random(15).NextBytes(data);
        var encoded = TestCompressor.CompressPurge(data);
        encoded[^1] ^= 0xFF;

        Assert.Throws<RvzHashMismatchException>(() => PurgeDecoder.Decode(encoded, [], data.Length));
    }

    [Fact]
    public void TruncatedStream_ThrowsFormatException()
    {
        var data = new byte[0x1000];
        new Random(16).NextBytes(data);
        var encoded = TestCompressor.CompressPurge(data);

        // Cutting inside a segment leaves the hash covering different bytes.
        Assert.ThrowsAny<RvzException>(() =>
            PurgeDecoder.Decode(encoded.AsSpan(0, encoded.Length - 5), [], data.Length));
        Assert.Throws<RvzFormatException>(() => PurgeDecoder.Decode([], [], 0x1000));
        Assert.Throws<RvzFormatException>(() =>
            PurgeDecoder.Decode([1, 2, 3], [], 0x1000)); // shorter than the trailer
    }

    [Fact]
    public void SegmentBeyondExpectedSize_Throws()
    {
        // The only non-zero byte sits at 0x1000, beyond the declared output size.
        var data = new byte[0x2000];
        data[0x1000] = 1;
        var encoded = TestCompressor.CompressPurge(data);

        Assert.Throws<RvzFormatException>(() => PurgeDecoder.Decode(encoded, [], 0x100));
    }
}
