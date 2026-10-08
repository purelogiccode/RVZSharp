using RVZSharp.Models;
using RVZSharp.Tests.Helpers;

namespace RVZSharp.Tests;

/// <summary>
/// Tests that <see cref="RvzReader.ReadAt"/> is thread-safe: concurrent random reads (raw
/// chunks and Wii partition regions with hash exceptions) must match the reference ISO.
/// </summary>
public class ConcurrentReadTests
{
    [Fact]
    public void ConcurrentReadAt_GameCube_MatchesReference()
    {
        // Many small raw chunks so concurrent readers contend on the same decoded units.
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x8000,
            DiscType = DiscType.GameCube,
            RawSize = 40 * 0x8000 + 0x12345,
            Seed = 7
        });

        AssertConcurrentReadsMatch(rvz, iso);
    }

    [Fact]
    public void ConcurrentReadAt_Wii_MatchesReference()
    {
        // Partition chunks with hash exceptions plus raw areas on both sides.
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x200000,
            DiscType = DiscType.Wii,
            RawSize = 0x18000,
            RawTailSize = 0x28000,
            Partition = new PartitionSpec { SectorCount = 70, Exceptions = MakeExceptions() },
            Seed = 3
        });

        AssertConcurrentReadsMatch(rvz, iso);
    }

    [Fact]
    public void ConcurrentReadAt_And_ReadFully_InterleaveSafely()
    {
        var (rvz, iso) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Lzma2,
            ChunkSize = 0x200000,
            DiscType = DiscType.Wii,
            RawSize = 0x18000,
            RawTailSize = 0x28000,
            Partition = new PartitionSpec { SectorCount = 70, Exceptions = MakeExceptions() },
            Seed = 5
        });
        using var reader = RvzReader.Open(new MemoryStream(rvz), leaveOpen: true);

        var failures = 0;
        Parallel.Invoke(
            () =>
            {
                if (!reader.ReadFully().AsSpan().SequenceEqual(iso))
                {
                    Interlocked.Increment(ref failures);
                }
            },
            () =>
            {
                var rng = new Random(99);
                var buffer = new byte[0x5000];
                for (var i = 0; i < 200; i++)
                {
                    var position = (long)(rng.NextDouble() * (iso.Length - buffer.Length));
                    var read = reader.ReadAt(position, buffer);
                    if (!iso.AsSpan((int)position, read).SequenceEqual(buffer.AsSpan(0, read)))
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            });

        Assert.Equal(0, failures);
    }

    private static void AssertConcurrentReadsMatch(byte[] rvz, byte[] iso)
    {
        using var reader = RvzReader.Open(new MemoryStream(rvz), leaveOpen: true);

        var failures = 0;
        Parallel.For(0, 8, task =>
        {
            var rng = new Random(1000 + task);
            var buffer = new byte[0x9000]; // spans chunk and 2 MiB region boundaries
            for (var i = 0; i < 300; i++)
            {
                var position = (long)(rng.NextDouble() * iso.Length);
                var count = rng.Next(1, buffer.Length);
                var read = reader.ReadAt(position, buffer.AsSpan(0, count));
                if (read <= 0 ||
                    !iso.AsSpan((int)position, read).SequenceEqual(buffer.AsSpan(0, read)))
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        Assert.Equal(0, failures);
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
