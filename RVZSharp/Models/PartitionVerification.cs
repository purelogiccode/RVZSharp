namespace RVZSharp.Models;

/// <summary>
/// The verification result of one Wii partition (Dolphin: VolumeVerifier's per-partition
/// checks): TMD validity, H3 table integrity and the per-sector hash tree walk.
/// </summary>
public sealed record PartitionVerification
{
    /// <summary>The partition the result belongs to.</summary>
    public required Partition Partition { get; init; }

    /// <summary>The partition name derived from its type ("game", "update", ...).</summary>
    public required string Name { get; init; }

    /// <summary>Number of data sectors (0x8000 bytes each) the partition declares.</summary>
    public long Blocks { get; init; }

    /// <summary>Number of data sectors whose hash tree was checked.</summary>
    public long VerifiedBlocks { get; init; }

    /// <summary>Number of data sectors whose hash tree did not match.</summary>
    public long FailedBlocks { get; init; }

    /// <summary>
    /// Whether the TMD (signature type, size, content table) is structurally valid; null when it
    /// could not be read or was not checked.
    /// </summary>
    public bool? TmdValid { get; init; }

    /// <summary>
    /// Whether the H3 table hashes to the TMD content hash; null when it was not checked.
    /// </summary>
    public bool? H3TableValid { get; init; }

    /// <summary>The problems found in this partition (empty when it is clean).</summary>
    public required IReadOnlyList<VerificationIssue> Issues { get; init; }

    /// <summary>True when no <see cref="VerificationSeverity.High"/> problems were found in this partition.</summary>
    public bool IsValid => Issues.All(issue => issue.Severity != VerificationSeverity.High);
}
