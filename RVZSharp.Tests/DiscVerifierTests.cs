using System.Security.Cryptography;
using RVZSharp.Blobs;
using RVZSharp.Models;
using RVZSharp.Verification;
using RVZSharp.Wii;

namespace RVZSharp.Tests;

/// <summary>
/// <see cref="DiscVerifier"/> tests: synthetic Wii ISOs with a full h0/h1/h2/h3 hash tree,
/// TMD and H3 table are verified cleanly, and corrupted data, H3 tables and TMDs are reported
/// with the right severity. Both unencrypted and encrypted partitions are covered.
/// </summary>
public class DiscVerifierTests
{
    private const int PartitionOffset = 0x100000;
    private const int DataOffset = 0x20000;
    private const int DataStart = PartitionOffset + DataOffset;
    private const int H3Offset = 0x1000;
    private const int TmdOffset = 0x400;
    private const int TmdSize = 0x208;
    private const int CertOffset = 0x700;
    private const int CertSize = 0x300;
    private const int SectorSize = 0x8000;
    private const int ClusterSectors = 64;
    private const int H3Size = 0x18000;

    [Fact]
    public void ValidWiiDisc_IsValid()
    {
        var iso = BuildWiiIso(1, encrypt: false);
        var report = Verify(iso);

        AssertValid(report);
        Assert.Equal(DiscType.Wii, report.DiscType);
        var partition = Assert.Single(report.Partitions);
        Assert.Equal("game", partition.Name);
        Assert.True(partition.TmdValid);
        Assert.True(partition.H3TableValid);
        Assert.Equal(ClusterSectors, partition.Blocks);
        Assert.Equal(ClusterSectors, partition.VerifiedBlocks);
        Assert.Equal(0, partition.FailedBlocks);
        Assert.Empty(partition.Issues);
        Assert.Equal(ClusterSectors, report.VerifiedBlocks);
    }

    [Fact]
    public void EncryptedWiiDisc_IsValid()
    {
        var iso = BuildWiiIso(1, encrypt: true);
        var report = Verify(iso);

        AssertValid(report);
        var partition = Assert.Single(report.Partitions);
        Assert.Equal(0, partition.FailedBlocks);
        Assert.Equal(ClusterSectors, partition.VerifiedBlocks);
    }

    [Fact]
    public void MultipleClusters_AreAllVerified()
    {
        var iso = BuildWiiIso(2, encrypt: false);
        var report = Verify(iso);

        AssertValid(report);
        var partition = Assert.Single(report.Partitions);
        Assert.Equal(2 * ClusterSectors, partition.VerifiedBlocks);
        Assert.Equal(2 * ClusterSectors, report.TotalBlocks);
    }

    [Fact]
    public void CorruptDataBlock_IsReportedAsHigh()
    {
        var iso = BuildWiiIso(1, encrypt: false);
        iso[DataStart + 5 * SectorSize + 0x400] ^= 0xFF; // first data byte of sector 5

        var report = Verify(iso);
        Assert.False(report.IsValid);
        var partition = Assert.Single(report.Partitions);
        Assert.Equal(1, partition.FailedBlocks);
        var issue = Assert.Single(partition.Issues);
        Assert.Equal(VerificationSeverity.High, issue.Severity);
        Assert.Contains("block 0x5", issue.Message);
        Assert.Contains("data block 0", issue.Message);
    }

    [Fact]
    public void CorruptHashArea_IsReportedAsHigh()
    {
        var iso = BuildWiiIso(1, encrypt: false);
        iso[DataStart + 3 * SectorSize + 0x10] ^= 0xFF; // h0[0] of sector 3

        var report = Verify(iso);
        Assert.False(report.IsValid);
        var partition = Assert.Single(report.Partitions);
        Assert.Equal(1, partition.FailedBlocks);
        Assert.Contains("block 0x3", partition.Issues[0].Message);
    }

    [Fact]
    public void H3TableNotMatchingTmd_IsReportedAsMedium()
    {
        var iso = BuildWiiIso(1, encrypt: false);
        iso[PartitionOffset + H3Offset] ^= 0xFF; // h3[0], TMD still has the original hash

        var report = Verify(iso);
        Assert.True(report.IsValid); // Medium issues do not invalidate the disc
        var partition = Assert.Single(report.Partitions);
        Assert.False(partition.H3TableValid);
        Assert.Equal(0, partition.FailedBlocks); // h0/h1/h2 still verify
        Assert.Contains(partition.Issues, issue =>
            issue.Severity == VerificationSeverity.Medium && issue.Message.Contains("H3"));
    }

    [Fact]
    public void BadTmdSignature_IsReportedAsMedium()
    {
        var iso = BuildWiiIso(1, encrypt: false);
        Array.Clear(iso, PartitionOffset + TmdOffset, 4); // wipe the signature type

        var report = Verify(iso);
        Assert.True(report.IsValid);
        var partition = Assert.Single(report.Partitions);
        Assert.False(partition.TmdValid);
        Assert.Null(partition.H3TableValid); // no content hash to compare against
        Assert.Equal(0, partition.FailedBlocks);
        Assert.Contains(partition.Issues, issue =>
            issue.Severity == VerificationSeverity.Medium && issue.Message.Contains("signature"));
    }

    [Fact]
    public void TruncatedPartitionData_IsReportedAsHigh()
    {
        var iso = BuildWiiIso(1, encrypt: false);
        // Declare 4× the data that exists.
        WriteBe32(iso, PartitionOffset + 0x2BC,
            (uint)((ClusterSectors * SectorSize * 4) >> 2));

        var report = Verify(iso);
        Assert.False(report.IsValid);
        var partition = Assert.Single(report.Partitions);
        Assert.Equal(0, partition.VerifiedBlocks);
        Assert.Contains(partition.Issues, issue => issue.Severity == VerificationSeverity.High);
    }

    [Fact]
    public void GameCubeDisc_IsValid()
    {
        var iso = new byte[0x20000];
        new Random(42).NextBytes(iso);
        iso[0x1C] = 0xC2;
        iso[0x1D] = 0x33;
        iso[0x1E] = 0x9F;
        iso[0x1F] = 0x3D;

        var report = Verify(iso);
        Assert.True(report.IsValid);
        Assert.Equal(DiscType.GameCube, report.DiscType);
        Assert.Empty(report.Partitions);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void NonDisc_IsReportedAsHigh()
    {
        var iso = new byte[0x20000];
        new Random(42).NextBytes(iso);

        var report = Verify(iso);
        Assert.False(report.IsValid);
        Assert.Equal(DiscType.Unknown, report.DiscType);
        Assert.Empty(report.Partitions);
        Assert.Equal(VerificationSeverity.High, Assert.Single(report.Issues).Severity);
    }

    [Fact]
    public void Progress_ReportsCompletion()
    {
        var iso = BuildWiiIso(1, encrypt: false);
        var progress = new List<double>();
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        DiscVerifier.Verify(blob, new InlineProgress(progress.Add));
        Assert.Equal(1.0, progress[^1]);
    }

    private static VerificationReport Verify(byte[] iso)
    {
        using var blob = PlainBlob.Open(new MemoryStream(iso));
        return DiscVerifier.Verify(blob);
    }

    /// <summary>Asserts validity with the full issue list in the failure message.</summary>
    private static void AssertValid(VerificationReport report)
    {
        var diagnostics = string.Join("; ", report.Issues
            .Concat(report.Partitions.SelectMany(partition => partition.Issues))
            .Select(issue => $"{issue.Severity}: {issue.Message}"));
        Assert.True(report.IsValid, diagnostics.Length == 0 ? "(no issues reported)" : diagnostics);
    }

    /// <summary>
    /// Builds a Wii ISO with one partition whose hash tree, H3 table and TMD are all consistent.
    /// The partition is unencrypted (the disc header says so) or AES-128-CBC encrypted.
    /// </summary>
    private static byte[] BuildWiiIso(int clusters, bool encrypt)
    {
        var dataSize = clusters * ClusterSectors * SectorSize;
        var imageSize = DataStart + dataSize + 0x1000;
        var iso = new byte[imageSize];
        new Random(99).NextBytes(iso);

        WriteBe32(iso, 0x18, 0x5D1C9EA3);
        iso[0x60] = 0; // hashes present
        iso[0x61] = encrypt ? (byte)0 : (byte)1; // encryption flag

        const int tableAddress = 0x40020;
        WriteBe32(iso, 0x40000, 1);
        WriteBe32(iso, 0x40004, tableAddress >> 2);
        for (var group = 1; group < 4; group++)
        {
            WriteBe32(iso, 0x40000 + group * 8, 0);
            WriteBe32(iso, 0x40004 + group * 8, 0);
        }

        WriteBe32(iso, tableAddress, (uint)(PartitionOffset >> 2));
        WriteBe32(iso, tableAddress + 4, 0);

        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 3)).ToArray();
        WriteBe32(iso, PartitionOffset, 0x10001); // ticket signature type
        key.CopyTo(iso, PartitionOffset + 0x1BF);
        WriteBe32(iso, PartitionOffset + 0x2A4, TmdSize);
        WriteBe32(iso, PartitionOffset + 0x2A8, TmdOffset >> 2);
        WriteBe32(iso, PartitionOffset + 0x2AC, CertSize);
        WriteBe32(iso, PartitionOffset + 0x2B0, CertOffset >> 2);
        WriteBe32(iso, PartitionOffset + 0x2B4, H3Offset >> 2);
        WriteBe32(iso, PartitionOffset + 0x2B8, DataOffset >> 2);
        WriteBe32(iso, PartitionOffset + 0x2BC, (uint)(dataSize >> 2));

        // Build every sector's data + h0 area, assemble the group's shared h1 (per 8 sectors)
        // and h2 (per cluster) arrays, derive the cluster's h3 entry, then store the
        // (optionally encrypted) sectors exactly like the reader's hash tree.
        var h3Table = new byte[H3Size];
        var rng = new Random(123);
        for (var cluster = 0; cluster < clusters; cluster++)
        {
            var datas = new byte[ClusterSectors][];
            var h0Areas = new byte[ClusterSectors][];
            for (var sector = 0; sector < ClusterSectors; sector++)
            {
                var data = new byte[0x7C00];
                rng.NextBytes(data);
                datas[sector] = data;

                var hashArea = new byte[0x400];
                WiiHashCalculator.BuildHashArea(data, hashArea);
                h0Areas[sector] = hashArea.AsSpan(0, 0x26C).ToArray();
            }

            var h1Arrays = new byte[8][];
            var h2Array = new byte[8 * 20];
            for (var group = 0; group < 8; group++)
            {
                var h1 = new byte[8 * 20];
                for (var j = 0; j < 8; j++)
                {
                    SHA1.HashData(h0Areas[group * 8 + j], h1.AsSpan(j * 20));
                }

                h1Arrays[group] = h1;
                SHA1.HashData(h1, h2Array.AsSpan(group * 20));
            }

            SHA1.HashData(h2Array, h3Table.AsSpan(cluster * 20));

            for (var sector = 0; sector < ClusterSectors; sector++)
            {
                var hashArea = new byte[0x400];
                h0Areas[sector].CopyTo(hashArea, 0);
                h1Arrays[sector / 8].CopyTo(hashArea, 0x280);
                h2Array.CopyTo(hashArea, 0x340);

                var stored = new byte[SectorSize];
                if (encrypt)
                {
                    EncryptSector(key, hashArea, datas[sector], stored);
                }
                else
                {
                    hashArea.CopyTo(stored, 0);
                    datas[sector].CopyTo(stored, 0x400);
                }

                stored.CopyTo(iso, DataStart + (cluster * ClusterSectors + sector) * SectorSize);
            }
        }

        h3Table.CopyTo(iso, PartitionOffset + H3Offset);

        // TMD: one content whose hash is the H3 table's SHA-1.
        var tmd = new byte[TmdSize];
        WriteBe32(tmd, 0, 0x10001);
        WriteBe16(tmd, 0x1DE, 1); // content count
        WriteBe32(tmd, 0x1E4, 0); // content id
        WriteBe16(tmd, 0x1E8, 0); // index
        WriteBe16(tmd, 0x1EA, 0); // type
        WriteBe64(tmd, 0x1EC, (ulong)dataSize + H3Size);
        SHA1.HashData(h3Table, tmd.AsSpan(0x1F4, 20));
        tmd.CopyTo(iso, PartitionOffset + TmdOffset);
        return iso;
    }

    private static void EncryptSector(byte[] key, byte[] hashArea, byte[] data, byte[] stored)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        var encryptedHash = new byte[0x400];
        aes.EncryptCbc(hashArea, new byte[16], encryptedHash, PaddingMode.None);
        var encryptedData = new byte[0x7C00];
        aes.EncryptCbc(data, encryptedHash.AsSpan(0x3D0, 16), encryptedData, PaddingMode.None);
        encryptedHash.CopyTo(stored, 0);
        encryptedData.CopyTo(stored, 0x400);
    }

    private static void WriteBe16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteBe64(byte[] data, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            data[offset + i] = (byte)(value >> (56 - 8 * i));
        }
    }

    /// <summary>Reports progress synchronously (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        private readonly Action<double> Report1 = report;
        public void Report(double value) => Report1(value);
    }
}
