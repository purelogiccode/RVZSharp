using System.Buffers.Binary;

namespace RVZSharp.IO;

/// <summary>
/// Big-endian reader over a <see cref="ReadOnlySpan{T}"/> of bytes, used to decode the
/// fixed-layout structures of the WIA/RVZ container (all integers are big endian).
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;

    /// <summary>Creates a big-endian reader over the given data, starting at position 0.</summary>
    /// <param name="data">The bytes to read from.</param>
    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    /// <summary>Number of bytes consumed so far.</summary>
    public int Position { get; private set; }

    /// <summary>Number of bytes that can still be read.</summary>
    public readonly int Remaining => _data.Length - Position;

    /// <summary>
    /// Reads a big-endian ushort and advances the position by 2. Throws an RvzFormatException
    /// when fewer than 2 bytes remain.
    /// </summary>
    /// <returns>The value read.</returns>
    public ushort ReadUInt16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(Position, 2));
        Position += 2;
        return value;
    }

    /// <summary>
    /// Reads a big-endian uint and advances the position by 4. Throws an RvzFormatException
    /// when fewer than 4 bytes remain.
    /// </summary>
    /// <returns>The value read.</returns>
    public uint ReadUInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(Position, 4));
        Position += 4;
        return value;
    }

    /// <summary>
    /// Reads a big-endian int and advances the position by 4. Throws an RvzFormatException
    /// when fewer than 4 bytes remain.
    /// </summary>
    /// <returns>The value read.</returns>
    public int ReadInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(Position, 4));
        Position += 4;
        return value;
    }

    /// <summary>
    /// Reads a big-endian ulong and advances the position by 8. Throws an RvzFormatException
    /// when fewer than 8 bytes remain.
    /// </summary>
    /// <returns>The value read.</returns>
    public ulong ReadUInt64()
    {
        Ensure(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(Position, 8));
        Position += 8;
        return value;
    }

    /// <summary>
    /// Reads count bytes and advances the position by count. Throws an RvzFormatException
    /// when fewer than count bytes remain.
    /// </summary>
    /// <param name="count">Number of bytes to read.</param>
    /// <returns>The bytes read.</returns>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Ensure(count);
        var value = _data.Slice(Position, count);
        Position += count;
        return value;
    }

    /// <summary>
    /// Reads a single byte and advances the position by 1. Throws an RvzFormatException
    /// when no bytes remain.
    /// </summary>
    /// <returns>The byte read.</returns>
    public byte ReadByte()
    {
        Ensure(1);
        return _data[Position++];
    }

    private readonly void Ensure(int count)
    {
        if (count > Remaining)
        {
            throw new RvzFormatException(
                $"Unexpected end of data: needed {count} more bytes, {Remaining} available.");
        }
    }
}
