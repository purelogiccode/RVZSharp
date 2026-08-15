using RVZSharp.Chunks;
using RVZSharp.Compression;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;
using ChunkDecodeResult = RVZSharp.Chunks.ChunkDecodeResult;

namespace RVZSharp.Tests;

public class ChunkDecoderTests
{
    private static byte[] Payload(int size, int seed)
    {
        var data = new byte[size];
        var rng = new Random(seed);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = i % 64 == 0 ? (byte)rng.Next(256) : (byte)(i * 31 % 251);
        }

        return data;
    }

    [Fact]
    public void Chunk_WithExtraStoredBytes_Throws()
    {
        // A group whose stored bytes exceed the expected payload must be rejected
        // (Dolphin: WIABlob.cpp:741-754) — the end-of-payload probe finds the excess.
        var payload = Payload(0x100, seed: 4);
        var (file, group) = GroupFile([.. payload, 0xAA], compressed: false);

        var ex = Assert.Throws<RvzFormatException>(() =>
            Decode(file, MakeDisc(CompressionType.None), CompressionType.None, group,
                isPartition: false, expectedSize: payload.Length, dataOffset: 0));
        Assert.Contains("more than", ex.Message);
    }

    [Fact]
    public void Chunk_CorruptBzip2_ThrowsFormatException()
    {
        // A corrupt bzip2 stream must surface as RvzFormatException, not leak a raw
        // SharpZipBaseException.
        var (file, group) = GroupFile([0x42, 0x5A, 0x68, 0x00, 0x00, 0xFF, 0xFF], compressed: true);

        Assert.Throws<RvzFormatException>(() =>
            Decode(file, MakeDisc(CompressionType.Bzip2), CompressionType.Bzip2, group,
                isPartition: false, expectedSize: 0x100, dataOffset: 0));
    }

    private static WiaDisc MakeDisc(CompressionType compression)
    {
        var builder = new TestDiscBuilder { ChunkSize = 0x8000 };
        switch (compression)
        {
            case CompressionType.Lzma:
            {
                var (props, _) = TestCompressor.EncodeLzma1([1], endMarker: true);
                props.CopyTo(builder.ComprData, 0);
                builder.ComprDataLen = (byte)props.Length;
                break;
            }
            case CompressionType.Lzma2:
                builder.ComprData[0] = 21;
                builder.ComprDataLen = 1;
                break;
        }

        builder.Compression = compression;
        return WiaDisc.Parse(builder.Build());
    }

    private static (MemoryStream File, RvzGroupEntry Group) GroupFile(byte[] data, bool compressed, uint packedSize = 0)
    {
        const int offset = 0x200;
        var file = new MemoryStream(offset + data.Length);
        file.Position = offset;
        file.Write(data);
        file.Position = 0;
        return (file, new RvzGroupEntry(offset / 4, (uint)data.Length | (compressed ? 0x80000000u : 0), packedSize));
    }

    private static ChunkDecodeResult Decode(Stream file, WiaDisc disc, CompressionType compression, RvzGroupEntry group,
        bool isPartition, int expectedSize, long dataOffset)
    {
        return ChunkDecoder.DecodeChunk(file, disc, CompressionCodecFactory.Create(compression),
            new ChunkDecodeRequest
            {
                Group = GroupEntry.FromRvz(group),
                IsPartition = isPartition,
                ExpectedSize = expectedSize,
                DataOffset = dataOffset
            });
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        var offset = 0;
        foreach (var a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }

        return result;
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Lzma2)]
    public void RawChunk_NoPacking_EveryCodec(CompressionType compression)
    {
        var payload = Payload(50_000, seed: 3);
        var stored = TestCompressor.Compress(compression, payload);
        var disc = MakeDisc(compression);
        var (file, group) = GroupFile(stored, compressed: compression != CompressionType.None);
        using (file)
        {
            var result = Decode(file, disc, compression, group, isPartition: false,
                expectedSize: payload.Length, dataOffset: 0x20000);
            Assert.Equal(payload, result.Payload);
        }
    }

    [Fact]
    public void ZeroGroup_ReturnsZeroes()
    {
        var disc = MakeDisc(CompressionType.Zstd);
        var (file, group) = GroupFile([], compressed: false);
        using (file)
        {
            var zeroGroup = new RvzGroupEntry(0, 0, 0);
            var result = Decode(file, disc, CompressionType.Zstd, zeroGroup, isPartition: false,
                expectedSize: 1000, dataOffset: 0);
            Assert.Equal(new byte[1000], result.Payload);
        }
    }
}
