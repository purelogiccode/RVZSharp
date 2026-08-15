namespace RVZSharp.Models;

/// <summary>The decoded content of one group chunk.</summary>
public readonly struct ChunkDecodeResult
{
    /// <summary>The chunk payload (exception lists and packing removed).</summary>
    public required byte[] Payload { get; init; }

    /// <summary>
    /// The parsed hash exception lists (partition chunks only; empty for raw data chunks).
    /// The lists themselves are not part of <see cref="Payload"/>.
    /// </summary>
    public HashExceptionEntry[][] ExceptionLists { get; init; } = [];

    /// <summary>Creates an empty chunk result; the required payload must be set by the caller.</summary>
    public ChunkDecodeResult()
    {
    }
}