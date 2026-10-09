using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

#pragma warning disable MA0004 // xUnit1030 forbids ConfigureAwait(false) in test methods

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the asynchronous API surface: <see cref="IBlobReader.ReadFullyAsync"/> /
/// <see cref="IBlobReader.CopyToAsync(Stream, IProgress{double}, CancellationToken)"/> and the
/// writers' <c>WriteAsync</c> forms must produce exactly the same bytes as their synchronous
/// counterparts and observe cancellation.
/// </summary>
public class AsyncApiTests
{
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

    /// <summary>Verifies that read fully async matches read fully.</summary>
    [Fact]
    public async Task ReadFullyAsync_MatchesReadFully()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(GcSpec());
        using IBlobReader reader = RvzReader.Open(new MemoryStream(rvz));

        var progress = new List<double>();
        var result = await reader.ReadFullyAsync(new SyncProgress<double>(progress.Add));

        Assert.Equal(iso, result);
        Assert.Equal(1.0, progress[^1]);
    }

    /// <summary>Verifies that copy to async parallel matches sequential.</summary>
    [Fact]
    public async Task CopyToAsync_Parallel_MatchesSequential()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(GcSpec());
        using IBlobReader reader = RvzReader.Open(new MemoryStream(rvz));

        using var destination = new MemoryStream();
        var copied = await reader.CopyToAsync(destination, null, 4);

        Assert.Equal(iso.Length, copied);
        Assert.Equal(iso, destination.ToArray());
    }

    /// <summary>Verifies that copy to async cancellation throws.</summary>
    [Fact]
    public async Task CopyToAsync_Cancellation_Throws()
    {
        var (rvz, _) = TestRvzBuilder.BuildWithIso(GcSpec());
        using IBlobReader reader = RvzReader.Open(new MemoryStream(rvz));
        using var cts = new CancellationTokenSource();

        using var destination = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.CopyToAsync(
            destination,
            new SyncProgress<double>(value =>
            {
                if (value >= 0.5)
                {
                    // ReSharper disable once AccessToDisposedClosure
                    cts.Cancel();
                }
            }), 4, cts.Token));
    }

    /// <summary>Verifies that RVZ write async matches write.</summary>
    [Fact]
    public async Task RvzWriteAsync_MatchesWrite()
    {
        var options = new RvzWriteOptions { Compression = CompressionType.Lzma2, ChunkSize = 0x8000 };

        var expected = EncodeSync((input, output) => RvzWriter.Write(input, output, options));
        var actual = await EncodeAsync((input, output) => RvzWriter.WriteAsync(input, output, options));

        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies that WIA write async matches write.</summary>
    [Fact]
    public async Task WiaWriteAsync_MatchesWrite()
    {
        var options = new RvzWriteOptions
        {
            Compression = CompressionType.Lzma2,
            ChunkSize = 0x200000
        };

        var expected = EncodeSync((input, output) => WiaWriter.Write(input, output, options));
        var actual = await EncodeAsync((input, output) => WiaWriter.WriteAsync(input, output, options));

        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies that GCZ write async matches write.</summary>
    [Fact]
    public async Task GczWriteAsync_MatchesWrite()
    {
        var expected = EncodeSync((input, output) => GczWriter.Write(input, output));
        var actual = await EncodeAsync((input, output) => GczWriter.WriteAsync(input, output));

        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies that write async cancellation throws.</summary>
    [Fact]
    public async Task WriteAsync_Cancellation_Throws()
    {
        var iso = BuildGcIso();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var input = PlainBlob.Open(new MemoryStream(iso));
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RvzWriter.WriteAsync(
            input, output, null, null, cts.Token));
    }

    private static byte[] EncodeSync(Action<IBlobReader, MemoryStream> write)
    {
        using var output = new MemoryStream();
        using var input = PlainBlob.Open(new MemoryStream(BuildGcIso()));
        write(input, output);
        return output.ToArray();
    }

    private static async Task<byte[]> EncodeAsync(
        Func<IBlobReader, MemoryStream, Task> writeAsync)
    {
        using var output = new MemoryStream();
        using var input = PlainBlob.Open(new MemoryStream(BuildGcIso()));
        await writeAsync(input, output);
        return output.ToArray();
    }

    private static RvzSpec GcSpec()
    {
        return new RvzSpec
        {
            Compression = CompressionType.Lzma2,
            ChunkSize = 0x8000,
            DiscType = DiscType.GameCube,
            RawSize = 8 * 0x8000 + 0x12345,
            Seed = 7
        };
    }

    private static byte[] BuildGcIso()
    {
        var iso = new byte[0x420000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;
        return iso;
    }
}
