using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the package-facing API surface: path-based opening, the default
/// <see cref="IBlobReader.ReadFully()"/> / <see cref="IBlobReader.CopyTo"/> implementations,
/// disc validation, and the writer's progress and cancellation support.
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

        public SyncProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
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
    public void ReadFully_WithProgress_WorksOnNonOverridingBlob()
    {
        var iso = MakeGcIso(0x10000);
        var gcz = TestLegacyBuilders.BuildGcz(iso);
        using IBlobReader blob = GczBlob.Open(new MemoryStream(gcz), leaveOpen: true);
        var progress = new List<double>();
        Assert.Equal(iso, blob.ReadFully(new SyncProgress<double>(progress.Add)));
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public void CopyTo_StreamsWholeImage_AndReportsMonotonicProgress()
    {
        var iso = MakeGcIso();
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        using var destination = new MemoryStream();
        var progress = new List<double>();

        var copied = blob.CopyTo(destination, new SyncProgress<double>(progress.Add));

        Assert.Equal(iso.Length, copied);
        Assert.Equal(iso, destination.ToArray());
        Assert.NotEmpty(progress);
        Assert.True(progress[0] > 0, "progress should start above zero");
        for (var i = 1; i < progress.Count; i++)
        {
            Assert.True(progress[i] >= progress[i - 1], "progress must be monotonic");
        }

        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public void CopyTo_ObservesCancellation_MidStream()
    {
        var iso = MakeGcIso();
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        using var destination = new MemoryStream();
        var cts = new CancellationTokenSource();
        try
        {
            var cancelOnFirstReport = true;
            var progress = new SyncProgress<double>(_ =>
            {
                if (cancelOnFirstReport)
                {
                    cancelOnFirstReport = false;
                    // ReSharper disable once AccessToDisposedClosure
                    cts.Cancel();
                }
            });

            Assert.ThrowsAny<OperationCanceledException>(() =>
                blob.CopyTo(destination, progress, cts.Token));
            Assert.True(destination.Length < iso.Length, "the copy should stop before the end");
        }
        finally
        {
            cts.Dispose();
        }
    }

    [Fact]
    public void RvzReader_ReadFully_WithProgress_AndCancellation()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(
            new RvzSpec { Compression = CompressionType.Zstd, RawSize = 0x8000 });
        using var reader = RvzReader.Open(new MemoryStream(rvz), leaveOpen: true);

        var progress = new List<double>();
        Assert.Equal(iso, reader.ReadFully(new SyncProgress<double>(progress.Add)));
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.ReadFully(null, cts.Token));
    }

    [Fact]
    public void RvzReader_CopyTo_StreamsByteExact()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(
            new RvzSpec { Compression = CompressionType.None, RawSize = 0x8000 });
        using var reader = RvzReader.Open(new MemoryStream(rvz), leaveOpen: true);
        using var destination = new MemoryStream();

        var copied = reader.CopyTo(destination);

        Assert.Equal(iso.Length, copied);
        Assert.Equal(iso, destination.ToArray());
    }

    [Fact]
    public void DiscHasher_ComputesKnownVectors()
    {
        // Standard test vectors for "123456789": CRC-32 = 0xCBF43926.
        var data = "123456789"u8.ToArray();
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(data), leaveOpen: true);
        var hashes = DiscHasher.Compute(blob);

        Assert.Equal(0xCBF43926u, hashes.Crc32);
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", Convert.ToHexString(hashes.Md5).ToLowerInvariant());
        Assert.Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441",
            Convert.ToHexString(hashes.Sha1).ToLowerInvariant());
    }

    [Fact]
    public void DiscHashes_Matches_ComparesAllThreeHashes()
    {
        var data = "123456789"u8.ToArray();
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(data), leaveOpen: true);
        var hashes = DiscHasher.Compute(blob);

        Assert.True(hashes.Matches(hashes));
        Assert.False(hashes.Matches(null));
        Assert.False(hashes.Matches(hashes with { Crc32 = hashes.Crc32 ^ 1 }));
        Assert.False(hashes.Matches(hashes with { Md5 = new byte[16] }));
        Assert.False(hashes.Matches(hashes with { Sha1 = new byte[20] }));
    }

    [Fact]
    public void DiscHasher_MatchesFrameworkHashes_AndReportsProgress()
    {
        var iso = MakeGcIso(0x10000);
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        var progress = new List<double>();
        var hashes = DiscHasher.Compute(blob, new SyncProgress<double>(progress.Add));

        Assert.Equal(SHA1.HashData(iso), hashes.Sha1);
        Assert.Equal(MD5.HashData(iso), hashes.Md5);
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public void DiscHasher_ObservesCancellation()
    {
        var iso = MakeGcIso();
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DiscHasher.Compute(blob, cancellationToken: cts.Token));
    }

    [Fact]
    public void Blob_DiscValidation_ClassifiesGameCubeWiiAndUnknown()
    {
        var gcIso = MakeGcIso();
        using (IBlobReader gc = PlainBlob.Open(new MemoryStream(gcIso), leaveOpen: true))
        {
            Assert.Equal(DiscType.GameCube, Blob.GetDiscType(gc));
            Assert.True(Blob.IsDisc(gc));
        }

        var (wiiRvz, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            DiscType = DiscType.Wii,
            Partition = new PartitionSpec()
        });
        using (var wii = RvzReader.Open(new MemoryStream(wiiRvz), leaveOpen: true))
        {
            Assert.Equal(DiscType.Wii, Blob.GetDiscType(wii));
            Assert.True(Blob.IsDisc(wii));
        }

        var garbage = new byte[0x420000];
        new Random(7).NextBytes(garbage);
        using (IBlobReader unknown = PlainBlob.Open(new MemoryStream(garbage), leaveOpen: true))
        {
            Assert.Equal(DiscType.Unknown, Blob.GetDiscType(unknown));
            Assert.False(Blob.IsDisc(unknown));
        }

        Assert.Throws<ArgumentNullException>(() => Blob.GetDiscType(null!));
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
        var cts = new CancellationTokenSource();
        try
        {
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                RvzWriter.Write(PlainBlob.Open(input, leaveOpen: true), output,
                    new RvzWriteOptions { Compression = CompressionType.None },
                    cancellationToken: cts.Token));
        }
        finally
        {
            cts.Dispose();
        }
    }

    [Fact]
    public void RvzWriter_Cancels_MidConversion()
    {
        var iso = MakeGcIso();
        using var input = new MemoryStream(iso);
        using var output = new MemoryStream();
        var cts = new CancellationTokenSource();
        try
        {
            var cancelOnFirstReport = true;
            var progress = new SyncProgress<double>(_ =>
            {
                if (cancelOnFirstReport)
                {
                    cancelOnFirstReport = false;
                    // ReSharper disable once AccessToDisposedClosure
                    cts.Cancel();
                }
            });

            Assert.ThrowsAny<OperationCanceledException>(() =>
                RvzWriter.Write(PlainBlob.Open(input, leaveOpen: true), output,
                    new RvzWriteOptions { Compression = CompressionType.None },
                    progress: progress, cancellationToken: cts.Token));
        }
        finally
        {
            cts.Dispose();
        }
    }
}
