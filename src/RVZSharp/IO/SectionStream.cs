namespace RVZSharp.IO;

/// <summary>
/// A read-only window over a section of an underlying stream. Reads never cross the section
/// bounds and disposing the window does not dispose the underlying stream.
/// </summary>
public sealed class SectionStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;
    private readonly long _length;

    public SectionStream(Stream baseStream, long start, long length)
    {
        if (start < 0 || length < 0 || start + length > baseStream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start),
                $"Section [{start}, {start + length}) is outside the stream of length {baseStream.Length}.");
        }

        _base = baseStream;
        _start = start;
        _length = length;
        Position = 0;
    }

    /// <summary>Length of the section.</summary>
    public override long Length => _length;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _base.Position - _start;
        set
        {
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _base.Position = _start + value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - Position;
        if (remaining <= 0)
        {
            return 0;
        }

        count = (int)Math.Min(count, remaining);
        return _base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - Position;
        if (remaining <= 0)
        {
            return 0;
        }

        buffer = buffer[..(int)Math.Min(buffer.Length, remaining)];
        return _base.Read(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = newPosition;
        return newPosition;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
