using RVZSharp.IO;

namespace RVZSharp.Chunks;

/// <summary>
/// <c>wia_exception_t</c>: one 20-byte difference between the recalculated hash data and the
/// original hash data of a Wii partition group.
/// </summary>
public readonly struct HashExceptionEntry
{
    public const int Size = 0x16;

    /// <summary>
    /// Offset among the hashes: 0x0000-0x0400 map to offsets 0x0000-0x0400 in the full 2 MiB,
    /// 0x0400-0x0800 map to 0x8000-0x8400, and so on. Restarts at 0 for each exception list.
    /// </summary>
    public ushort Offset { get; }

    /// <summary>The hash that replaces the automatically generated one at <see cref="Offset"/>.</summary>
    public byte[] Hash { get; } // 20 bytes

    public HashExceptionEntry(ushort offset, byte[] hash)
    {
        Offset = offset;
        Hash = hash;
    }

    public static HashExceptionEntry Parse(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        var offset = reader.ReadUInt16();
        var hash = reader.ReadBytes(20).ToArray();
        return new HashExceptionEntry(offset, hash);
    }
}
