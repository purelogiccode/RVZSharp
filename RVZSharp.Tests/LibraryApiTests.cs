using RVZSharp;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the package-facing API surface: path-based opening, the default
/// <see cref="IBlobReader.ReadFully"/> implementation, and the writer's progress and
/// cancellation support.
/// </summary>
public class LibraryApiTests
{
    /// <summary>
    /// Synchronous progress reporter: unlike <see cref="Progress{T}"/> it invokes the handler
    /// on the calling thread, which makes progress assertions deterministic in tests.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
    private static byte[] MakeGcIso(int size = 0x420000)
    {
        var iso = new byte[size];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D; // GameCube DVD magic
        return iso;
    }

    [Fact]
    public void Blob_Open_ByPath_DetectsAndDecodes()
    {
        var iso = MakeGcIso();
        var path = Path.Combine(Path.GetTempPath(), $"rvzsharp-api-{Guid.NewGuid():N}.iso");
        try
        {
            File.WriteAllBytes(path, iso);
            using var blob = Blob.Open(path);
            Assert.Equal(BlobType.Plain, blob.Type);
            Assert.Equal(iso, blob.ReadFully());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Blob_Open_ByPath_OpensRvzAndDecodesByteExact()
    {
        var iso = MakeGcIso();
        var path = Path.Combine(Path.GetTempPath(), $"rvzsharp-api-{Guid.NewGuid():N}.rvz");
        try
        {
            using (var input = new MemoryStream(iso))
            using (var output = File.Create(path))
            {
                RvzWriter.Write(PlainBlob.Open(input, leaveOpen: true), output,
                    new RvzWriteOptions { Compression = CompressionType.Zstd, CompressionLevel = 3 });
            }

            using var blob = Blob.Open(path);
            Assert.Equal(BlobType.Rvz, blob.Type);
            Assert.Equal(iso, blob.ReadFully());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Blob_Open_ByPath_WithNfsKey_Decodes()
    {
        var key = new byte[16];
        new Random(5).NextBytes(key);
        var (nfs, iso) = TestLegacyBuilders.BuildNfs(key, blockCount: 3,
            ranges: [(0, 3)]);
        var path = Path.Combine(Path.GetTempPath(), $"rvzsharp-api-{Guid.NewGuid():N}.nfs");
        try
        {
            File.WriteAllBytes(path, nfs);
            using var blob = Blob.Open(path, key);
            Assert.Equal(BlobType.Nfs, blob.Type);
            Assert.Equal(iso, blob.ReadFully());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFully_DefaultImplementation_WorksOnNonOverridingBlob()
    {
        var iso = MakeGcIso(0x10000);
        var gcz = TestLegacyBuilders.BuildGcz(iso);
        using IBlobReader blob = GczBlob.Open(new MemoryStream(gcz), leaveOpen: true);
        Assert.Equal(BlobType.Gcz, blob.Type);
        Assert.Equal(iso, blob.ReadFully());
    }

    [Fact]
    public void RvzWriter_Reports_MonotonicProgress_EndingAtOne()
    {
        var iso = MakeGcIso();
        var progress = new List<double>();
        using var input = new MemoryStream(iso);
        using var output = new MemoryStream();
        RvzWriter.Write(PlainBlob.Open(input, leaveOpen: true), output,
            new RvzWriteOptions { Compression = CompressionType.None },
            progress: new SyncProgress<double>(progress.Add));

        Assert.NotEmpty(progress);
        Assert.True(progress[0] > 0, "progress should start above zero");
        for (var i = 1; i < progress.Count; i++)
        {
            Assert.True(progress[i] >= progress[i - 1], "progress must be monotonic");
        }

        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public void RvzWriter_Observes_Cancellation()
    {
        var iso = MakeGcIso();
        using var input = new MemoryStream(iso);
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            RvzWriter.Write(PlainBlob.Open(input, leaveOpen: true), output,
                new RvzWriteOptions { Compression = CompressionType.None },
                cancellationToken: cts.Token));
    }

    [Fact]
    public void RvzWriter_Cancels_MidConversion()
    {
        var iso = MakeGcIso();
        using var input = new MemoryStream(iso);
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var cancelOnFirstReport = true;
        var progress = new SyncProgress<double>(_ =>
        {
            if (cancelOnFirstReport)
            {
                cancelOnFirstReport = false;
                cts.Cancel(); // deterministic: the next chunk read throws
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            RvzWriter.Write(PlainBlob.Open(input, leaveOpen: true), output,
                new RvzWriteOptions { Compression = CompressionType.None },
                progress: progress, cancellationToken: cts.Token));
    }
}
