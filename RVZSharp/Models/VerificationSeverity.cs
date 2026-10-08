namespace RVZSharp.Models;

/// <summary>How severe a <see cref="VerificationIssue"/> is; only <see cref="High"/> makes a report invalid.</summary>
public enum VerificationSeverity
{
    /// <summary>Cosmetic or informational (e.g. a data size that is not block-aligned).</summary>
    Low,

    /// <summary>A structural problem that may affect emulation (e.g. a bad TMD or H3 table).</summary>
    Medium,

    /// <summary>Corrupt or unusable data (e.g. a partition header or hash tree that fails).</summary>
    High
}
