using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the parallel full-image decode path: <see cref="RvzReader.CopyTo(Stream, IProgress{double}?, int, CancellationToken)"/>
/// must produce byte-identical output to the sequential copy for any thread count, including
/// multi-batch images, Wii partitions with hash exceptions and WIA files. Other formats fall
/// back to the sequential default interface implementation.
/// </summary>
public class ParallelDecodeTests
{
    /// <summary>
    /// Synchronous progress reporter: unlike <see cref="Progress{T}"/> it invokes the handler
    /// on the calling thread, which makes progress assertions deterministic in tests.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }

    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Lzma2)]
    public void GameCube_ParallelCopy_MatchesSequential(CompressionType compression)
    {
        // 41 raw chunks of 32 KiB: several parallel batches with four threads.
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = compression,
            ChunkSize = 0x8000,
            DiscType = DiscType.GameCube,
            RawSize = 40 * 0x8000 + 0x12345,
            Seed = 7
        });
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        using var sequential = new MemoryStream();
        reader.CopyTo(sequential);
        Assert.Equal(iso, sequential.ToArray());

        foreach (var threads in new[] { 2, 4 })
        {
            using var parallel = new MemoryStream();
            var progress = new List<double>();
            reader.CopyTo(parallel, new SyncProgress<double>(progress.Add), threads);
            Assert.Equal(iso, parallel.ToArray());
            Assert.Equal(1.0, progress[^1]);
        }
    }

    [Theory]
    [InlineData(CompressionType.Zstd)]
    [InlineData(CompressionType.Lzma2)]
    public void Wii_ParallelCopy_MatchesSequential(CompressionType compression)
    {
        // Wii partition regions with hash exceptions plus raw areas on both sides.
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = compression,
            ChunkSize = 0x200000,
            DiscType = DiscType.Wii,
            RawSize = 0x18000,
            RawTailSize = 0x28000,
            Partition = new PartitionSpec { SectorCount = 70, Exceptions = MakeExceptions() },
            Seed = 3
        });
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        using var parallel = new MemoryStream();
        reader.CopyTo(parallel, null, 4);
        Assert.Equal(iso, parallel.ToArray());
    }

    [Fact]
    public void Wii_MultiRegionChunks_ParallelCopy_MatchesSequential()
    {
        // 4 MiB chunks span two 2 MiB regions per partition chunk (two exception lists).
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Lzma2,
            ChunkSize = 0x400000,
            DiscType = DiscType.Wii,
            RawSize = 0x18000,
            RawTailSize = 0x28000,
            Partition = new PartitionSpec { SectorCount = 130, Exceptions = MakeExceptions() },
            Seed = 3
        });
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        using var parallel = new MemoryStream();
        reader.CopyTo(parallel, null, 4);
        Assert.Equal(iso, parallel.ToArray());
    }

    [Fact]
    public void Wia_ParallelCopy_MatchesSequential()
    {
        // WIA chunk sizes are multiples of 2 MiB; nine chunks span two parallel batches.
        var (wia, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Lzma2,
            ChunkSize = 0x200000,
            DiscType = DiscType.GameCube,
            RawSize = 8 * 0x200000 + 0x12345,
            IsWia = true,
            Seed = 7
        });
        using var reader = RvzReader.OpenWia(new MemoryStream(wia));

        using var parallel = new MemoryStream();
        reader.CopyTo(parallel, null, 4);
        Assert.Equal(iso, parallel.ToArray());
    }

    [Fact]
    public void ParallelCopy_ReportsProgressAndEndsAtOne()
    {
        var (rvz, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x8000,
            DiscType = DiscType.GameCube,
            RawSize = 40 * 0x8000 + 0x12345,
            Seed = 7
        });
        using var reader = RvzReader.Open(new MemoryStream(rvz));

        var progress = new List<double>();
        using var destination = new MemoryStream();
        reader.CopyTo(destination, new SyncProgress<double>(progress.Add), 4);

        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.Order()), "progress must be monotonic");
    }

    [Fact]
    public void ParallelCopy_Cancellation_Throws()
    {
        var (rvz, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x8000,
            DiscType = DiscType.GameCube,
            RawSize = 40 * 0x8000 + 0x12345,
            Seed = 7
        });
        using var reader = RvzReader.Open(new MemoryStream(rvz));
        using var cts = new CancellationTokenSource();

        using var destination = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => reader.CopyTo(destination,
            new SyncProgress<double>(value =>
            {
                if (value >= 0.5)
                {
                    cts.Cancel();
                }
            }), 4, cts.Token));
    }

    [Fact]
    public void NonRvzFormat_IgnoresThreadCount()
    {
        // The default IBlobReader implementation decodes sequentially for every other format.
        var iso = new byte[0x420000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;
        using IBlobReader blob = Blobs.PlainBlob.Open(new MemoryStream(iso));

        using var destination = new MemoryStream();
        blob.CopyTo(destination, null, 4);
        Assert.Equal(iso, destination.ToArray());
    }

    private static HashExceptionEntry[][] MakeExceptions()
    {
        var e0 = new[]
        {
            new HashExceptionEntry(0x100, [.. Enumerable.Range(0, 20).Select(i => (byte)i)]),
            new HashExceptionEntry(0x3E0, [.. Enumerable.Range(0, 20).Select(i => (byte)(0x80 + i))])
        };
        var e1 = new[]
        {
            new HashExceptionEntry(0x200, [.. Enumerable.Range(0, 20).Select(i => (byte)(0x40 + i))])
        };
        return [e0, e1];
    }
}
