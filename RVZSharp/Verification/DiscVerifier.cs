using System.Security.Cryptography;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;

namespace RVZSharp.Verification;

/// <summary>
/// Verifies a decoded disc image like Dolphin's VolumeVerifier: every Wii partition's header,
/// TMD structure, H3 table and per-sector h0/h1/h2/h3 hash tree are checked, and every problem
/// is reported with its severity. GameCube discs have no hash trees and are reported as valid;
/// container integrity (SHA-1s, structure) is already validated when the reader opens a file.
/// </summary>
public static class DiscVerifier
{
    private const int SectorSize = 0x8000;
    private const int DataBlockSize = 0x400;
    private const int HashBlockSize = 0x400;
    private const int BlocksPerSector = 31;
    private const int HashSize = 20;
    private static readonly byte[] ZeroIv = new byte[16];
    private const int H0Size = BlocksPerSector * HashSize; // 0x26C
    private const int H1Offset = 0x280;
    private const int H1Size = 8 * HashSize; // 0xA0
    private const int H2Offset = 0x340;
    private const int H2Size = 8 * HashSize; // 0xA0
    private const int H3Size = 0x18000;
    private const int IvOffset = 0x3D0;

    private const int TmdMinSize = 0x1E4;
    private const int TmdMaxSize = 0x49E4;
    private const int TmdContentCountOffset = 0x1DE;
    private const int TmdContentsOffset = 0x1E4;
    private const int TmdContentSize = 0x24;
    private const int TmdContentHashOffset = TmdContentsOffset + 0x10;

    private const int MaxDetailedBlockIssues = 4;

    /// <summary>
    /// Verifies the disc image served by <paramref name="disc"/>.
    /// </summary>
    /// <param name="disc">A decoded GameCube or Wii disc image (any container format).</param>
    /// <param name="progress">
    /// Optional progress reporter; receives a fraction in [0, 1] of the data sectors checked.
    /// </param>
    /// <param name="cancellationToken">Cancellation is observed between sectors.</param>
    /// <returns>The per-partition results and every problem found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="disc"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static VerificationReport Verify(IBlobReader disc, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);

        var discType = WiiVolume.GetDiscType(disc);
        if (discType == DiscType.Unknown)
        {
            return new VerificationReport
            {
                DiscType = discType,
                Partitions = [],
                Issues =
                [
                    new VerificationIssue(VerificationSeverity.High,
                        "The image is not a GameCube or Wii disc (no disc header magic at 0x18/0x1C).")
                ]
            };
        }

        // GameCube discs have no partition table or hash trees; the container readers already
        // validate what structure they have.
        if (discType == DiscType.GameCube)
        {
            return new VerificationReport
            {
                DiscType = discType,
                Partitions = [],
                Issues = []
            };
        }

        var issues = new List<VerificationIssue>();
        var partitions = WiiVolume.GetPartitions(disc);
        if (partitions.Count == 0)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.High,
                "The disc has no valid Wii partitions."));
        }

        var hashes = WiiVolume.HasWiiHashes(disc);
        var encryption = WiiVolume.HasWiiEncryption(disc);

        // First pass: parse every partition header so progress can cover the whole disc.
        var parsed = new List<(Partition Partition, string Name, PartitionHeader? Header,
            List<VerificationIssue> Issues)>();
        var totalBlocks = 0L;
        foreach (var partition in partitions)
        {
            var partitionIssues = new List<VerificationIssue>();
            var header = ParseHeader(disc, partition, partitionIssues);
            if (header != null)
            {
                totalBlocks += header.Blocks;
            }

            parsed.Add((partition, GetPartitionName(partition.Type), header, partitionIssues));
        }

        // Second pass: TMD, H3 table and the per-sector hash tree walk.
        var results = new List<PartitionVerification>();
        var verifiedBlocks = 0L;
        foreach (var (partition, name, header, partitionIssues) in parsed)
        {
            bool? tmdValid = null;
            bool? h3TableValid = null;
            var verified = 0L;
            var failed = 0L;

            if (header != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tmdValid = CheckTmd(disc, partition, header, partitionIssues, out var contentHash);
                if (hashes)
                {
                    var h3Table = ReadH3Table(disc, partition, header, partitionIssues);
                    h3TableValid = CheckH3Table(h3Table, contentHash, partition, partitionIssues);
                    (verified, failed) = VerifyBlocks(disc, partition, name, header, encryption,
                        h3Table, h3TableValid == true, partitionIssues, ref verifiedBlocks,
                        totalBlocks, progress, cancellationToken);
                }
            }

            results.Add(new PartitionVerification
            {
                Partition = partition,
                Name = name,
                Blocks = header?.Blocks ?? 0,
                VerifiedBlocks = verified,
                FailedBlocks = failed,
                TmdValid = tmdValid,
                H3TableValid = h3TableValid,
                Issues = partitionIssues
            });
        }

        progress?.Report(1.0);
        return new VerificationReport
        {
            DiscType = discType,
            Partitions = results,
            Issues = issues,
            TotalBlocks = totalBlocks,
            VerifiedBlocks = verifiedBlocks
        };
    }

    /// <summary>
    /// Reads and structurally checks a partition header. Returns null when the data area is
    /// missing or outside the image (block verification is then impossible).
    /// </summary>
    private static PartitionHeader? ParseHeader(IBlobReader disc, Partition partition,
        List<VerificationIssue> issues)
    {
        if ((partition.Offset & (SectorSize - 1)) != 0 ||
            ((partition.Offset + partition.DataOffset) & (SectorSize - 1)) != 0)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition is not properly aligned.", partition.Offset, partition.Type));
        }

        Span<byte> header = stackalloc byte[0x2C0];
        if (partition.Offset + (ulong)header.Length > (ulong)disc.Length ||
            disc.ReadAt((long)partition.Offset, header) != header.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.High,
                "The partition header could not be read.", partition.Offset, partition.Type));
            return null;
        }

        var tmdSize = ReadBe32(header, 0x2A4);
        var tmdOffset = (ulong)ReadBe32(header, 0x2A8) << 2;
        var certSize = ReadBe32(header, 0x2AC);
        var certOffset = (ulong)ReadBe32(header, 0x2B0) << 2;
        var h3Offset = (ulong)ReadBe32(header, 0x2B4) << 2;

        if (tmdSize == 0 || tmdOffset == 0 ||
            partition.Offset + tmdOffset + tmdSize > (ulong)disc.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.High,
                "The partition TMD is missing or outside the image.", partition.Offset, partition.Type));
        }

        if (certSize == 0 || certOffset == 0 ||
            partition.Offset + certOffset + certSize > (ulong)disc.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition certificate chain is missing or outside the image.",
                partition.Offset, partition.Type));
        }

        if (partition.DataSize == 0 ||
            partition.Offset + partition.DataOffset + partition.DataSize > (ulong)disc.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.High,
                "The partition data area is missing or outside the image.",
                partition.Offset, partition.Type));
            return null;
        }

        if (partition.DataSize % SectorSize != 0)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Low,
                "The partition data size is not evenly divisible by the sector size.",
                partition.Offset, partition.Type));
        }

        return new PartitionHeader(tmdOffset, tmdSize, h3Offset, partition.DataSize);
    }

    /// <summary>
    /// Reads the TMD and checks its signature type, size and content table (Dolphin:
    /// TMDReader::IsValid). The first content's hash is returned for the H3 comparison.
    /// </summary>
    private static bool CheckTmd(IBlobReader disc, Partition partition, PartitionHeader header,
        List<VerificationIssue> issues, out byte[]? contentHash)
    {
        contentHash = null;
        if (header.TmdSize < TmdMinSize || header.TmdSize > TmdMaxSize)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                $"The partition TMD size {header.TmdSize} is invalid.", partition.Offset, partition.Type));
            return false;
        }

        if (partition.Offset + header.TmdOffset + header.TmdSize > (ulong)disc.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition TMD is outside the image.", partition.Offset, partition.Type));
            return false;
        }

        var tmd = new byte[header.TmdSize];
        if (disc.ReadAt((long)(partition.Offset + header.TmdOffset), tmd) != tmd.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition TMD could not be read.", partition.Offset, partition.Type));
            return false;
        }

        // RSA2048/RSA4096/ECC signature types; the structural checks below assume the RSA2048
        // header layout Dolphin parses.
        var signatureType = ReadBe32(tmd, 0);
        if (signatureType is not (0x10000 or 0x10001 or 0x10002))
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                $"The partition TMD has an unrecognized signature type 0x{signatureType:X}.",
                partition.Offset, partition.Type));
            return false;
        }

        var contentCount = ReadBe16(tmd, TmdContentCountOffset);
        if (contentCount == 0)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition TMD declares no contents.", partition.Offset, partition.Type));
            return false;
        }

        if ((long)TmdContentsOffset + (long)contentCount * TmdContentSize > tmd.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition TMD content table is truncated.", partition.Offset, partition.Type));
            return false;
        }

        contentHash = tmd.AsSpan(TmdContentHashOffset, HashSize).ToArray();
        return true;
    }

    /// <summary>Reads the 0x18000-byte H3 table, reporting a problem when it is out of bounds.</summary>
    private static byte[]? ReadH3Table(IBlobReader disc, Partition partition, PartitionHeader header,
        List<VerificationIssue> issues)
    {
        if (header.H3Offset == 0 ||
            partition.Offset + header.H3Offset + H3Size > (ulong)disc.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition H3 table is missing or outside the image.",
                partition.Offset, partition.Type));
            return null;
        }

        var h3Table = new byte[H3Size];
        if (disc.ReadAt((long)(partition.Offset + header.H3Offset), h3Table) != h3Table.Length)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition H3 table could not be read.", partition.Offset, partition.Type));
            return null;
        }

        return h3Table;
    }

    /// <summary>
    /// Checks the H3 table against the TMD content hash (Dolphin: CheckH3TableIntegrity).
    /// Returns null when either side is unavailable.
    /// </summary>
    private static bool? CheckH3Table(byte[]? h3Table, byte[]? contentHash, Partition partition,
        List<VerificationIssue> issues)
    {
        if (h3Table == null || contentHash == null)
        {
            return null;
        }

        Span<byte> actual = stackalloc byte[HashSize];
        SHA1.HashData(h3Table, actual);
        if (!actual.SequenceEqual(contentHash))
        {
            issues.Add(new VerificationIssue(VerificationSeverity.Medium,
                "The partition H3 hash table does not match the TMD content hash.",
                partition.Offset, partition.Type));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Walks every data sector of a partition and checks its h0/h1/h2 hashes against the
    /// embedded hash area and (when the H3 table is valid) its h2 against the H3 entry
    /// (Dolphin: CheckBlockIntegrity).
    /// </summary>
    private static (long Verified, long Failed) VerifyBlocks(IBlobReader disc, Partition partition,
        string name, PartitionHeader header, bool encryption, byte[]? h3Table, bool checkH3,
        List<VerificationIssue> issues, ref long verifiedBlocks, long totalBlocks,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var blocks = header.Blocks;
        var sector = new byte[SectorSize];
        var data = new byte[BlocksPerSector * DataBlockSize];
        var hashArea = new byte[HashBlockSize];
        Aes? aes = null;
        if (encryption)
        {
            aes = Aes.Create();
            aes.Key = partition.Key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
        }

        var verified = 0L;
        var failed = 0L;
        try
        {
            for (var block = 0L; block < blocks; block++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = (long)(partition.Offset + partition.DataOffset + (ulong)block * SectorSize);
                if (disc.ReadAt(offset, sector) != sector.Length)
                {
                    issues.Add(new VerificationIssue(VerificationSeverity.High,
                        $"The {name} partition ended early at block 0x{block:X}.",
                        partition.Offset, partition.Type));
                    break;
                }

                if (aes != null)
                {
                    aes.DecryptCbc(sector.AsSpan(HashBlockSize, data.Length),
                        sector.AsSpan(IvOffset, 16), data, PaddingMode.None);
                    aes.DecryptCbc(sector.AsSpan(0, HashBlockSize), ZeroIv,
                        hashArea.AsSpan(0, HashBlockSize), PaddingMode.None);
                }
                else
                {
                    sector.AsSpan(HashBlockSize, data.Length).CopyTo(data);
                    sector.AsSpan(0, HashBlockSize).CopyTo(hashArea);
                }

                if (!CheckSectorHashes(block, data, hashArea, h3Table, checkH3, out var detail))
                {
                    failed++;
                    if (failed <= MaxDetailedBlockIssues)
                    {
                        issues.Add(new VerificationIssue(VerificationSeverity.High,
                            $"The {name} partition block 0x{block:X} failed hash verification ({detail}).",
                            partition.Offset, partition.Type));
                    }
                }

                verified++;
                verifiedBlocks++;
                if ((block & 0x3F) == 0x3F && totalBlocks > 0)
                {
                    progress?.Report((double)verifiedBlocks / totalBlocks);
                }
            }
        }
        finally
        {
            aes?.Dispose();
        }

        if (failed > MaxDetailedBlockIssues)
        {
            issues.Add(new VerificationIssue(VerificationSeverity.High,
                $"{failed} of {blocks} {name} partition blocks failed hash verification.",
                partition.Offset, partition.Type));
        }

        return (verified, failed);
    }

    /// <summary>Checks one sector's data against its embedded hash area and the H3 entry.</summary>
    private static bool CheckSectorHashes(long block, byte[] data, byte[] hashArea, byte[]? h3Table,
        bool checkH3, out string? detail)
    {
        Span<byte> hash = stackalloc byte[HashSize];

        for (var i = 0; i < BlocksPerSector; i++)
        {
            SHA1.HashData(data.AsSpan(i * DataBlockSize, DataBlockSize), hash);
            if (!hash.SequenceEqual(hashArea.AsSpan(i * HashSize, HashSize)))
            {
                detail = $"data block {i}";
                return false;
            }
        }

        SHA1.HashData(hashArea.AsSpan(0, H0Size), hash);
        if (!hash.SequenceEqual(hashArea.AsSpan(H1Offset + (int)(block % 8) * HashSize, HashSize)))
        {
            detail = "h1";
            return false;
        }

        SHA1.HashData(hashArea.AsSpan(H1Offset, H1Size), hash);
        if (!hash.SequenceEqual(hashArea.AsSpan(H2Offset + (int)(block / 8 % 8) * HashSize, HashSize)))
        {
            detail = "h2";
            return false;
        }

        if (checkH3 && h3Table != null)
        {
            SHA1.HashData(hashArea.AsSpan(H2Offset, H2Size), hash);
            var index = block / 64 * HashSize;
            if (index + HashSize > h3Table.Length ||
                !hash.SequenceEqual(h3Table.AsSpan((int)index, HashSize)))
            {
                detail = "h3";
                return false;
            }
        }

        detail = null;
        return true;
    }

    private static string GetPartitionName(uint type)
    {
        return type switch
        {
            0 => "game",
            1 => "update",
            2 => "channel",
            3 => "install",
            _ => $"type 0x{type:X}"
        };
    }

    private static ushort ReadBe16(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadBe32(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    /// <summary>The partition header fields the verifier needs.</summary>
    private sealed record PartitionHeader(ulong TmdOffset, uint TmdSize, ulong H3Offset, ulong DataSize)
    {
        /// <summary>Number of data sectors (0x8000 bytes each).</summary>
        public long Blocks => (long)(DataSize / SectorSize);
    }
}
