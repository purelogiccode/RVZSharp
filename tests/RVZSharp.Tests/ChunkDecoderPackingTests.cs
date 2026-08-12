using RVZSharp;
using RVZSharp.Chunks;
using RVZSharp.Compression;
using RVZSharp.Format;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

public partial class ChunkDecoderPackingTests
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

    private static WiaDisc MakeDisc(CompressionType compression)
    {
        var builder = new TestDiscBuilder { ChunkSize = 0x8000 };
        if (compression == CompressionType.Lzma)
        {
            var (props, _) = TestCompressor.EncodeLzma1([1], endMarker: true);
            props.CopyTo(builder.ComprData, 0);
            builder.ComprDataLen = (byte)props.Length;
        }
        else if (compression == CompressionType.Lzma2)
        {
            builder.ComprData[0] = 21;
            builder.ComprDataLen = 1;
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
        return (file, new RvzGroupEntry((uint)(offset / 4), (uint)data.Length | (compressed ? 0x80000000u : 0), packedSize));
    }

    private static ChunkDecodeResult Decode(Stream file, WiaDisc disc, CompressionType compression, RvzGroupEntry group,
        bool isPartition, int expectedSize, long dataOffset) =>
        ChunkDecoder.DecodeChunk(file, disc, CompressionCodecFactory.Create(compression),
            new ChunkDecodeRequest
            {
                Group = group,
                IsPartition = isPartition,
                ExpectedSize = expectedSize,
                DataOffset = dataOffset,
            });

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

    private static byte[] Be32(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] WithExceptionList(byte[] payload, bool alignTo4)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0);
        ms.WriteByte(0); // one empty list
        if (alignTo4)
        {
            while (ms.Position % 4 != 0)
            {
                ms.WriteByte(0);
            }
        }

        ms.Write(payload);
        return ms.ToArray();
    }
}

public partial class ChunkDecoderPackingTests
{
    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Bzip2)]
    [InlineData(CompressionType.Lzma)]
    [InlineData(CompressionType.Lzma2)]
    public void RawChunk_WithPacking_EveryCodec(CompressionType compression)
    {
        var payload = Payload(40_000, seed: 5);
        var literalPart = payload.AsSpan(0, 10_000).ToArray();
        var paddedPart = payload.AsSpan(10_000).ToArray();
        var seed = new byte[68];
        new Random(8).NextBytes(seed);
        var junk = ReferencePrng.Generate(seed, 0x20000, paddedPart.Length);
        // The seed must reproduce the padding: re-seed until it matches.
        if (!junk.SequenceEqual(paddedPart))
        {
            // Generate a seed that reproduces this exact data is not feasible; instead use
            // data that the seed DOES reproduce.
            var packedAlt = Concat(
                Be32((uint)literalPart.Length), literalPart,
                Be32(0x8000_0000u | (uint)junk.Length), seed);
            var storedAlt = TestCompressor.Compress(compression, packedAlt);
            var discAlt = MakeDisc(compression);
            var (fileAlt, groupAlt) = GroupFile(storedAlt, compressed: compression != CompressionType.None,
                packedSize: (uint)packedAlt.Length);
            using (fileAlt)
            {
                var result = Decode(fileAlt, discAlt, compression, groupAlt, isPartition: false,
                    expectedSize: literalPart.Length + junk.Length, dataOffset: 0x20000);
                Assert.Equal(Concat(literalPart, junk), result.Payload);
            }

            return;
        }

        var packed = Concat(
            Be32((uint)literalPart.Length), literalPart,
            Be32(0x8000_0000u | (uint)paddedPart.Length), seed);
        var stored = TestCompressor.Compress(compression, packed);
        var disc = MakeDisc(compression);
        var (file, group) = GroupFile(stored, compressed: compression != CompressionType.None,
            packedSize: (uint)packed.Length);
        using (file)
        {
            var result = Decode(file, disc, compression, group, isPartition: false,
                expectedSize: payload.Length, dataOffset: 0x20000);
            Assert.Equal(payload, result.Payload);
        }
    }

    [Fact]
    public void PartitionChunk_NoExceptions_Compressed()
    {
        var payload = Payload(0x1F000, seed: 9);
        var stored = TestCompressor.Compress(CompressionType.Zstd, Concat([0x00, 0x00], payload));
        var disc = MakeDisc(CompressionType.Zstd);
        var (file, group) = GroupFile(stored, compressed: true);
        using (file)
        {
            var result = Decode(file, disc, CompressionType.Zstd, group, isPartition: true,
                expectedSize: payload.Length, dataOffset: 0x100000);
            Assert.Equal(payload, result.Payload);
        }
    }

    [Fact]
    public void PartitionChunk_WithExceptions_Compressed()
    {
        var payload = Payload(0x1F000, seed: 12);
        var exceptions = new byte[2 + 22];
        exceptions[0] = 0x00; // count = 1
        exceptions[1] = 0x01;
        exceptions[2] = 0x01; // entry offset = 0x0123
        exceptions[3] = 0x23;
        payload.AsSpan(0, 20).CopyTo(exceptions.AsSpan(4));
        var stored = TestCompressor.Compress(CompressionType.Zstd, Concat(exceptions, payload));
        var disc = MakeDisc(CompressionType.Zstd);
        var (file, group) = GroupFile(stored, compressed: true);
        using (file)
        {
            var result = Decode(file, disc, CompressionType.Zstd, group, isPartition: true,
                expectedSize: payload.Length, dataOffset: 0x100000);
            Assert.Equal(payload, result.Payload);
        }
    }

    [Fact]
    public void PartitionChunk_WithExceptions_None_AlignsTo4()
    {
        var payload = Payload(20_000, seed: 2);
        var stored = WithExceptionList(payload, alignTo4: true);
        var disc = MakeDisc(CompressionType.None);
        var (file, group) = GroupFile(stored, compressed: false);
        using (file)
        {
            var result = Decode(file, disc, CompressionType.None, group, isPartition: true,
                expectedSize: payload.Length, dataOffset: 0x100000);
            Assert.Equal(payload, result.Payload);
        }
    }

    [Fact]
    public void PartitionChunk_TruncatedExceptions_Throws()
    {
        var disc = MakeDisc(CompressionType.Zstd);
        var truncated = TestCompressor.Compress(CompressionType.Zstd, [0x00, 0x05]);
        var (file, group) = GroupFile(truncated, compressed: true); // declares 5 exceptions, none present
        using (file)
        {
            Assert.Throws<RvzFormatException>(() => Decode(file, disc, CompressionType.Zstd, group,
                isPartition: true, expectedSize: 0x1F000, dataOffset: 0));
        }
    }

    [Fact]
    public void ExceptionListCount_MatchesDolphinFormula()
    {
        // 2 MiB container → partition chunk 0x1F0000 → 1 list
        Assert.Equal(1, ChunkDecoder.ExceptionListCount(0x1F0000));
        // 4 MiB container → partition chunk 0x3E0000 → 2 lists
        Assert.Equal(2, ChunkDecoder.ExceptionListCount(0x3E0000));
        // 32 KiB container → partition chunk 0x1F000 → 1 list
        Assert.Equal(1, ChunkDecoder.ExceptionListCount(0x1F000));
    }
}
