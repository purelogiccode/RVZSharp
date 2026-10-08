namespace RVZSharp.Models;

/// <summary>
/// One problem found by <see cref="Verification.DiscVerifier.Verify(Interfaces.IBlobReader, IProgress{double}, CancellationToken)"/>.
/// Disc-level problems leave <see cref="PartitionOffset"/> and <see cref="PartitionType"/> null.
/// </summary>
/// <param name="Severity">How severe the problem is.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="PartitionOffset">The partition the problem belongs to, or null for disc-level problems.</param>
/// <param name="PartitionType">The partition type, or null for disc-level problems.</param>
public sealed record VerificationIssue(
    VerificationSeverity Severity,
    string Message,
    ulong? PartitionOffset = null,
    uint? PartitionType = null);
