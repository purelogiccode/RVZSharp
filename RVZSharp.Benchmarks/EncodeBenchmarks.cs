using BenchmarkDotNet.Attributes;
using RVZSharp.Blobs;
using RVZSharp.Models;

namespace RVZSharp.Benchmarks;

/// <summary>
/// Encode throughput for every writer/codec on a 16 MiB synthetic GameCube image
/// (chunk/block size 128 KiB). The Zstd case is the baseline.
/// </summary>
[MemoryDiagnoser]
public class EncodeBenchmarks
{
    private byte[] _iso = null!;

    [GlobalSetup]
    public void Setup()
    {
        _iso = BenchmarkDisc.BuildGameCubeImage();
    }

    [Benchmark(Baseline = true)]
    public long Rvz_Zstd()
    {
        return EncodeRvz(CompressionType.Zstd, 3);
    }

    [Benchmark]
    public long Rvz_Lzma2()
    {
        return EncodeRvz(CompressionType.Lzma2, 3);
    }

    [Benchmark]
    public long Rvz_Bzip2()
    {
        return EncodeRvz(CompressionType.Bzip2, 3);
    }

    [Benchmark]
    public long Rvz_None()
    {
        return EncodeRvz(CompressionType.None, 0);
    }

    [Benchmark]
    public long Rvz_Zstd_NoPacking()
    {
        return EncodeRvz(CompressionType.Zstd, 3, packing: false);
    }

    [Benchmark]
    public long Wia_Lzma2()
    {
        return EncodeWia();
    }

    [Benchmark]
    public long Gcz_Deflate()
    {
        return EncodeGcz();
    }

    private long EncodeRvz(CompressionType compression, int level, bool packing = true)
    {
        using var input = PlainBlob.Open(new MemoryStream(_iso));
        using var output = new MemoryStream();
        RvzWriter.Write(input, output, new RvzWriteOptions
        {
            Compression = compression,
            CompressionLevel = level,
            ChunkSize = 0x20000,
            Packing = packing
        });
        return output.Length;
    }

    private long EncodeWia()
    {
        using var input = PlainBlob.Open(new MemoryStream(_iso));
        using var output = new MemoryStream();
        WiaWriter.Write(input, output, new RvzWriteOptions
        {
            Compression = CompressionType.Lzma2,
            CompressionLevel = 3,
            ChunkSize = 0x200000
        });
        return output.Length;
    }

    private long EncodeGcz()
    {
        using var input = PlainBlob.Open(new MemoryStream(_iso));
        using var output = new MemoryStream();
        GczWriter.Write(input, output, new GczWriteOptions { BlockSize = 0x4000 });
        return output.Length;
    }
}
