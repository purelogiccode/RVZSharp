using BenchmarkDotNet.Attributes;
using RVZSharp.Blobs;
using RVZSharp.Models;

namespace RVZSharp.Benchmarks;

/// <summary>
/// Full-image decode throughput (one streaming pass through <see cref="DiscHasher"/>, which
/// decodes every byte) for each container/codec. The RVZ/Zstd case is the baseline.
/// </summary>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    private byte[] _rvzZstd = null!;
    private byte[] _rvzLzma2 = null!;
    private byte[] _wiaLzma2 = null!;
    private byte[] _gcz = null!;

    [GlobalSetup]
    public void Setup()
    {
        var iso = BenchmarkDisc.BuildGameCubeImage();

        _rvzZstd = Encode(iso, output => RvzWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), output, new RvzWriteOptions
            {
                Compression = CompressionType.Zstd,
                CompressionLevel = 3,
                ChunkSize = 0x20000
            }));
        _rvzLzma2 = Encode(iso, output => RvzWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), output, new RvzWriteOptions
            {
                Compression = CompressionType.Lzma2,
                CompressionLevel = 3,
                ChunkSize = 0x20000
            }));
        _wiaLzma2 = Encode(iso, output => WiaWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), output, new RvzWriteOptions
            {
                Compression = CompressionType.Lzma2,
                CompressionLevel = 3,
                ChunkSize = 0x200000
            }));
        _gcz = Encode(iso, output => GczWriter.Write(
            PlainBlob.Open(new MemoryStream(iso)), output, new GczWriteOptions
            {
                BlockSize = 0x4000
            }));
    }

    [Benchmark(Baseline = true)]
    public uint Decode_Rvz_Zstd() => Hash(_rvzZstd);

    [Benchmark]
    public uint Decode_Rvz_Lzma2() => Hash(_rvzLzma2);

    [Benchmark]
    public uint Decode_Wia_Lzma2() => Hash(_wiaLzma2);

    [Benchmark]
    public uint Decode_Gcz() => Hash(_gcz);

    private static byte[] Encode(byte[] iso, Action<MemoryStream> write)
    {
        using var output = new MemoryStream();
        write(output);
        return output.ToArray();
    }

    private static uint Hash(byte[] image)
    {
        using var blob = Blob.Open(new MemoryStream(image));
        return DiscHasher.Compute(blob).Crc32;
    }
}
