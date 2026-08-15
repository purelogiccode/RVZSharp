using System.Buffers.Binary;
using RVZSharp;

namespace RVZSharp.IO;

/// <summary>
/// Big-endian reader over a <see cref="ReadOnlySpan{T}"/> of bytes, used to decode the
/// fixed-layout structures of the WIA/RVZ container (all integers are big endian).
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;

    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    /// <summary>Number of bytes consumed so far.</summary>
    public int Position { get; private set; }

    /// <summary>Number of bytes that can still be read.</summary>
    public int Remaining => _data.Length - Position;

    public ushort ReadUInt16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(Position, 2));
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(Position, 4));
        Position += 4;
        return value;
    }

    public int ReadInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(Position, 4));
        Position += 4;
        return value;
    }

    public ulong ReadUInt64()
    {
        Ensure(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(Position, 8));
        Position += 8;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Ensure(count);
        var value = _data.Slice(Position, count);
        Position += count;
        return value;
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _data[Position++];
    }

    private void Ensure(int count)
    {
        if (count > Remaining)
        {
            throw new RvzFormatException(
                $"Unexpected end of data: needed {count} more bytes, {Remaining} available.");
        }
    }
}
