using System.Runtime.InteropServices;

namespace RVZSharp.Models;

/// <summary>What is needed to decode one group chunk.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ChunkDecodeRequest
{
    /// <summary>The group entry describing where and how this chunk is stored.</summary>
    public required GroupEntry Group { get; init; }

    /// <summary>True for Wii partition chunks (they start with hash exception lists).</summary>
    public required bool IsPartition { get; init; }

    /// <summary>Expected payload size in bytes (chunk size, or less for the last chunk).</summary>
    public required int ExpectedSize { get; init; }

    /// <summary>
    /// Offset of this chunk's data: disc-relative for raw data, partition-data-relative for
    /// partitions. Used for the PRNG skip in RVZ packing.
    /// </summary>
    public required long DataOffset { get; init; }
}