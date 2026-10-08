using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Tests for the package-facing API surface: path-based opening, the default
/// <see cref="IBlobReader.ReadFully()"/> / <see cref="IBlobReader.CopyTo(Stream, IProgress{double}, CancellationToken)"/> implementations,
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
    public void DiscHasher_PrefixOverload_HashesOnlyTheRequestedBytes()
    {
        var iso = MakeGcIso(0x10000);
        var prefix = iso.AsSpan(0, 0x8000).ToArray();
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);

        var hashes = DiscHasher.Compute(blob, prefix.Length);
        Assert.Equal(SHA1.HashData(prefix), hashes.Sha1);
        Assert.Equal(MD5.HashData(prefix), hashes.Md5);
        Assert.NotEqual(DiscHasher.Compute(blob).Sha1, hashes.Sha1);
    }

    [Fact]
    public void DiscHasher_LengthOutsideTheImage_IsRejected()
    {
        var iso = MakeGcIso(0x1000);
        using IBlobReader blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscHasher.Compute(blob, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscHasher.Compute(blob, iso.Length + 1));
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

    [Fact]
    public void Blob_Open_SplitPlainIso_ConcatenatesParts()
    {
        var iso = MakeGcIso(0x10000);
        var directory = Directory.CreateTempSubdirectory("rvzsharp-split-");
        try
        {
            var part0 = Path.Combine(directory.FullName, "game.part0.iso");
            File.WriteAllBytes(part0, iso[..0x8000]);
            File.WriteAllBytes(Path.Combine(directory.FullName, "game.part1.iso"), iso[0x8000..]);

            using var blob = Blob.Open(part0);
            Assert.Equal(BlobType.Plain, blob.Type);
            Assert.Equal(iso.Length, blob.Length);
            Assert.Equal(iso, blob.ReadFully());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Blob_Open_SplitPlainIso_StreamOverload_WithPath()
    {
        var iso = MakeGcIso(0x10000);
        var directory = Directory.CreateTempSubdirectory("rvzsharp-split-");
        try
        {
            var part0 = Path.Combine(directory.FullName, "game.part0.iso");
            File.WriteAllBytes(part0, iso[..0x4000]);
            File.WriteAllBytes(Path.Combine(directory.FullName, "game.part1.iso"), iso[0x4000..]);

            using var stream = File.OpenRead(part0);
            using var blob = Blob.Open(stream, filePath: part0, leaveOpen: true);
            Assert.Equal(iso.Length, blob.Length);
            Assert.Equal(iso, blob.ReadFully());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Blob_Open_SplitPlainIso_ZeroSizeContinuation_FallsBackToFirstPart()
    {
        var iso = MakeGcIso(0x10000);
        var directory = Directory.CreateTempSubdirectory("rvzsharp-split-");
        try
        {
            var part0 = Path.Combine(directory.FullName, "game.part0.iso");
            File.WriteAllBytes(part0, iso);
            File.WriteAllBytes(Path.Combine(directory.FullName, "game.part1.iso"), []);

            using var blob = Blob.Open(part0);
            Assert.Equal(iso.Length, blob.Length);
            Assert.Equal(iso, blob.ReadFully());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BlobStream_ReadsSeeksAndCopies()
    {
        var iso = MakeGcIso(0x10000);
        using var blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        using var stream = new BlobStream(blob, leaveOpen: true);

        Assert.Equal(iso.Length, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);

        // Seek + read at an offset.
        stream.Seek(0x1234, SeekOrigin.Begin);
        var buffer = new byte[0x100];
        Assert.Equal(buffer.Length, stream.Read(buffer));
        Assert.Equal(iso.AsSpan(0x1234, buffer.Length).ToArray(), buffer);
        Assert.Equal(0x1234 + buffer.Length, stream.Position);

        // Seek from the end + copy the tail.
        stream.Seek(-0x100, SeekOrigin.End);
        using var tail = new MemoryStream();
        stream.CopyTo(tail);
        Assert.Equal(iso[^0x100..], tail.ToArray());

        // Reading at the end returns 0.
        Assert.Equal(0, stream.Read(new byte[16]));
    }

    [Fact]
    public void BlobStream_Dispose_ClosesBlobUnlessLeaveOpen()
    {
        var iso = MakeGcIso(0x1000);

        var closed = PlainBlob.Open(new MemoryStream(iso));
        new BlobStream(closed).Dispose();
        Assert.Throws<ObjectDisposedException>(() => closed.ReadAt(0, new byte[4]));

        var kept = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        new BlobStream(kept, leaveOpen: true).Dispose();
        Assert.Equal(4, kept.ReadAt(0, new byte[4]));
    }

    [Fact]
    public void BlobStream_Write_IsNotSupported()
    {
        var iso = MakeGcIso(0x1000);
        using var blob = PlainBlob.Open(new MemoryStream(iso), leaveOpen: true);
        using var stream = new BlobStream(blob, leaveOpen: true);

        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
    }

    [Fact]
    public void WriterPathOverloads_RoundTrip_ThroughReaders()
    {
        var iso = MakeGcIso(0x10000);
        var directory = Directory.CreateTempSubdirectory("rvzsharp-paths-");
        try
        {
            var isoPath = Path.Combine(directory.FullName, "game.iso");
            var rvzPath = Path.Combine(directory.FullName, "game.rvz");
            var wiaPath = Path.Combine(directory.FullName, "game.wia");
            var gczPath = Path.Combine(directory.FullName, "game.gcz");
            File.WriteAllBytes(isoPath, iso);

            RvzWriter.Write(isoPath, rvzPath, new RvzWriteOptions { Compression = CompressionType.None });
            using (var rvz = RvzReader.Open(rvzPath))
            {
                Assert.Equal(iso, rvz.ReadFully());
            }

            WiaWriter.Write(isoPath, wiaPath, new RvzWriteOptions { Compression = CompressionType.None });
            using (var wia = RvzReader.OpenWia(wiaPath))
            {
                Assert.Equal(iso, wia.ReadFully());
            }

            GczWriter.Write(isoPath, gczPath, new GczWriteOptions());
            using (IBlobReader gcz = GczBlob.Open(File.OpenRead(gczPath), leaveOpen: false))
            {
                Assert.Equal(iso, gcz.ReadFully());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
