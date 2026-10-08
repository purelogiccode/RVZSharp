namespace RVZSharp.Models;

/// <summary>
/// The result of <see cref="Verification.DiscVerifier.Verify(Interfaces.IBlobReader, IProgress{double}, CancellationToken)"/>:
/// the disc type, every Wii partition's result and the disc-level problems.
/// </summary>
public sealed record VerificationReport
{
    /// <summary>The detected disc type.</summary>
    public required DiscType DiscType { get; init; }

    /// <summary>The verified partitions (empty for GameCube discs and non-disc images).</summary>
    public required IReadOnlyList<PartitionVerification> Partitions { get; init; }

    /// <summary>The disc-level problems (empty when the disc structure is clean).</summary>
    public required IReadOnlyList<VerificationIssue> Issues { get; init; }

    /// <summary>Total data sectors across all partitions.</summary>
    public long TotalBlocks { get; init; }

    /// <summary>Data sectors whose hash tree was checked.</summary>
    public long VerifiedBlocks { get; init; }

    /// <summary>
    /// True when no <see cref="VerificationSeverity.High"/> problems were found anywhere.
    /// GameCube discs have no hash trees and are always valid here.
    /// </summary>
    public bool IsValid =>
        Issues.All(issue => issue.Severity != VerificationSeverity.High) &&
        Partitions.All(partition => partition.IsValid);
}
