using BenchmarkDotNet.Attributes;
using RVZSharp.Blobs;
using RVZSharp.Models;

namespace RVZSharp.Benchmarks;

/// <summary>
/// Writer thread-scaling for Zstd (128 KiB chunks, 16 MiB image = 128 groups, so parallel
/// batches matter). <c>Threads = 0</c> uses the processor count. The output is byte-identical
/// for every value; only the wall time changes.
/// </summary>
[MemoryDiagnoser]
public class ThreadScalingBenchmarks
{
    private byte[] _iso = null!;

    [Params(1, 2, 4, 8, 0)] public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _iso = BenchmarkDisc.BuildGameCubeImage();
    }

    [Benchmark]
    public long Rvz_Zstd()
    {
        using var input = PlainBlob.Open(new MemoryStream(_iso));
        using var output = new MemoryStream();
        RvzWriter.Write(input, output, new RvzWriteOptions
        {
            Compression = CompressionType.Zstd,
            CompressionLevel = 3,
            ChunkSize = 0x20000,
            MaxThreads = Threads
        });
        return output.Length;
    }
}
