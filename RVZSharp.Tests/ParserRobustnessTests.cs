using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Tests.Helpers;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

/// <summary>
/// Mutation/fuzz-style robustness tests: corrupted or hostile inputs must fail with an
/// <see cref="RvzException"/>, never with an unhandled exception type (index/overflow/OOM)
/// or an unbounded loop.
/// </summary>
public class ParserRobustnessTests
{
    /// <summary>
    /// Mutations per format (default 80). Set <c>RVZSHARP_FUZZ_ITERATIONS</c> to run a
    /// deeper local fuzzing pass, e.g. <c>RVZSHARP_FUZZ_ITERATIONS=2000</c>.
    /// </summary>
    private static int MutationsPerFormat =>
        int.TryParse(Environment.GetEnvironmentVariable("RVZSHARP_FUZZ_ITERATIONS"), out var n) && n > 0
            ? n
            : 80;

    private sealed record Sample(string Name, byte[] Data, byte[]? NfsKey);

    private static List<Sample> BuildSamples()
    {
        var gcIso = MakeGcIso();
        var (rvz, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Zstd,
            ChunkSize = 0x200000,
            RawSize = 3 * 0x200000 + 0x1234,
            Seed = 7
        });
        var (wia, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Lzma2,
            ChunkSize = 0x200000,
            RawSize = 3 * 0x200000 + 0x1234,
            IsWia = true,
            Seed = 9
        });
        var (wiiRvz, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.Bzip2,
            ChunkSize = 0x200000,
            DiscType = DiscType.Wii,
            RawSize = 0x18000,
            RawTailSize = 0x28000,
            Partition = new PartitionSpec { SectorCount = 70 },
            Seed = 11
        });
        var gcz = TestLegacyBuilders.BuildGcz(gcIso);
        var ciso = TestLegacyBuilders.BuildCiso(gcIso, 0x8000, [0, 1, 2, 3]);
        var wbfs = TestLegacyBuilders.BuildWbfs(gcIso);
        var (tgc, _) = TestLegacyBuilders.BuildTgc();
        var key = new byte[16];
        new Random(5).NextBytes(key);
        var (nfs, _) = TestLegacyBuilders.BuildNfs(key, blockCount: 3, ranges: [(0, 3)]);

        return
        [
            new Sample("RVZ", rvz, null),
            new Sample("WIA", wia, null),
            new Sample("RVZ-Wii", wiiRvz, null),
            new Sample("GCZ", gcz, null),
            new Sample("CISO", ciso, null),
            new Sample("WBFS", wbfs, null),
            new Sample("TGC", tgc, null),
            new Sample("NFS", nfs, key)
        ];
    }

    private static byte[] MakeGcIso()
    {
        var iso = new byte[0x400000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;
        return iso;
    }

    [Fact]
    public void MutatedFiles_FailOnlyWithRvzExceptions()
    {
        var failures = new List<string>();
        var sampleIndex = 0;
        foreach (var sample in BuildSamples())
        {
            var rng = new Random(0x5EED + sampleIndex++);
            for (var i = 0; i < MutationsPerFormat; i++)
            {
                var mutated = Mutate(sample.Data, rng, i);
                Probe(mutated, sample, failures, $"{sample.Name} mutation {i}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void GarbageWithContainerMagic_FailsOnlyWithRvzExceptions()
    {
        var failures = new List<string>();
        var magics = new (string Name, byte[] Magic)[]
        {
            ("RVZ", "RVZ\x01"u8.ToArray()),
            ("WIA", "WIA\x01"u8.ToArray()),
            ("CISO", "CISO"u8.ToArray()),
            ("GCZ", [0x01, 0xC0, 0x0B, 0xB1]),
            ("WBFS", "WBFS"u8.ToArray()),
            ("TGC", [0xAE, 0x0F, 0x38, 0xA2]),
            ("NFS", "EGGS"u8.ToArray())
        };

        var rng = new Random(0xF00D);
        foreach (var (name, magic) in magics)
        {
            for (var i = 0; i < MutationsPerFormat; i++)
            {
                var data = new byte[rng.Next(4, 0x40000)];
                rng.NextBytes(data);
                magic.CopyTo(data, 0);
                var sample = new Sample(name, data, new byte[16]);
                Probe(data, sample, failures, $"{name} garbage {i}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void PlainIso_WithHugeWiiPartitionCount_FailsWithFormatException()
    {
        // A plain ISO is never hash-validated, so the partition-table cap must protect it.
        var iso = new byte[0x50000];
        new Random(13).NextBytes(iso);
        WriteBe32(iso, 0x18, WiiVolume.WII_MAGIC);
        WriteBe32(iso, 0x40000, 0xFFFFFFF0); // hostile group-0 entry count
        WriteBe32(iso, 0x40004, 0x40008 >> 2); // ...with a readable table offset
        using var blob = PlainBlob.Open(new MemoryStream(iso));

        Assert.Throws<RvzFormatException>(() => WiiVolume.GetPartitions(blob));
    }

    [Fact]
    public void Rvz_WithHugeTableCounts_IsRejectedBeforeAllocating()
    {
        // Craft a structurally valid RVZ whose disc struct declares millions of groups.
        // The disc hash is recomputed, so the count check (not the hash check) must fire.
        var (rvz, _) = TestRvzBuilder.BuildWithIso(new RvzSpec
        {
            Compression = CompressionType.None,
            ChunkSize = 0x8000,
            RawSize = 0x10000,
            Seed = 7
        });

        // NumGroups lives at disc offset 0x80 + 68 (big-endian u32); the disc struct starts
        // right after the 0x48-byte file head.
        var discStart = WiaFileHead.Size;
        var discSize = (int)ReadBe32(rvz, 0x0C);
        WriteBe32(rvz, discStart + 0x80 + 68, 0x0FFFFFFF);

        // Recompute the disc hash and the file-head hash so the count check (not the hash
        // check) is what rejects the file.
        SHA1.HashData(rvz.AsSpan(discStart, discSize)).CopyTo(rvz, 0x10);
        SHA1.HashData(rvz.AsSpan(0, WiaFileHead.FileHeadHashOffset)).CopyTo(rvz, WiaFileHead.FileHeadHashOffset);

        using var stream = new MemoryStream(rvz);
        Assert.Throws<RvzFormatException>(() => RvzReader.Open(stream));
    }

    private static byte[] Mutate(byte[] data, Random rng, int mode)
    {
        switch (mode % 4)
        {
            case 0:
            {
                var length = rng.Next(1, data.Length);
                return data[..length];
            }

            case 1:
            {
                var copy = (byte[])data.Clone();
                var flips = rng.Next(1, 8);
                for (var i = 0; i < flips; i++)
                {
                    copy[rng.Next(copy.Length)] ^= (byte)rng.Next(1, 256);
                }

                return copy;
            }

            case 2:
            {
                var appended = new byte[data.Length + rng.Next(1, 64)];
                data.CopyTo(appended, 0);
                rng.NextBytes(appended.AsSpan(data.Length));
                return appended;
            }

            default:
            {
                var patched = (byte[])data.Clone();
                if (patched.Length >= 8)
                {
                    var offset = rng.Next(4, patched.Length - 4);
                    patched[offset] = 0xFF;
                    patched[offset + 1] = 0xFF;
                    patched[offset + 2] = 0xFF;
                    patched[offset + 3] = 0xFF;
                }

                return patched;
            }
        }
    }

    private static void Probe(byte[] data, Sample sample, List<string> failures, string label)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var blob = sample.NfsKey is null
                ? Blob.Open(stream, filePath: null, leaveOpen: true)
                : Blob.Open(stream, sample.NfsKey, leaveOpen: true);

            var buffer = new byte[0x4000];
            for (long position = 0; position < blob.Length && position < 0x80000; position += 0x2111)
            {
                if (blob.ReadAt(position, buffer) <= 0)
                {
                    break;
                }
            }

            if (blob.Length <= 0x400000)
            {
                blob.ReadFully();
            }
        }
        catch (RvzException)
        {
            // Expected for malformed input.
        }
        catch (Exception e)
        {
            failures.Add($"{label}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }

    private static uint ReadBe32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8)
                      | data[offset + 3]);
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
