namespace RVZSharp.IO;

/// <summary>
/// A read-only window over a section of an underlying stream. Reads never cross the section
/// bounds and disposing the window does not dispose the underlying stream.
/// </summary>
public sealed class SectionStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;

    public SectionStream(Stream baseStream, long start, long length)
    {
        // A section outside the stream means a truncated/corrupt container: report it as a
        // format error (Dolphin's OffsetRead fails cleanly, WIABlob.cpp:168-171, 698) instead
        // of leaking an ArgumentOutOfRangeException out of RvzReader.Open.
        if (start < 0 || length < 0 || start + length > baseStream.Length)
        {
            throw new RvzFormatException(
                $"Section [{start}, {start + length}) is outside the stream of length {baseStream.Length}.");
        }

        _base = baseStream;
        _start = start;
        Length = length;
        Position = 0;
    }

    /// <summary>Length of the section.</summary>
    public override long Length { get; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Position
    {
        // The base stream may have been seeked externally; never report a position outside
        // the section.
        get => Math.Clamp(_base.Position - _start, 0, Length);
        set
        {
            if (value < 0 || value > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _base.Position = _start + value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // The base stream may have been seeked externally: never read outside the section
        // (return 0 instead of crossing the section bounds).
        if (_base.Position < _start || _base.Position >= _start + Length)
        {
            return 0;
        }

        var remaining = _start + Length - _base.Position;
        count = (int)Math.Min(count, remaining);
        return _base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        // The base stream may have been seeked externally: never read outside the section
        // (return 0 instead of crossing the section bounds).
        if (_base.Position < _start || _base.Position >= _start + Length)
        {
            return 0;
        }

        var remaining = _start + Length - _base.Position;
        buffer = buffer[..(int)Math.Min(buffer.Length, remaining)];
        return _base.Read(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = newPosition;
        return newPosition;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
