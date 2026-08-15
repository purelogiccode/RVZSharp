namespace RVZSharp.IO;

/// <summary>
/// A read-only window over a section of an underlying stream. Reads never cross the section
/// bounds and disposing the window does not dispose the underlying stream.
/// </summary>
public sealed class SectionStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;

    /// <summary>
    /// Creates a read-only window over a section of the given stream. The section must lie
    /// within the stream, otherwise an RvzFormatException is thrown.
    /// </summary>
    /// <param name="baseStream">The underlying stream to read from.</param>
    /// <param name="start">Offset of the section within the underlying stream.</param>
    /// <param name="length">Length of the section.</param>
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

    /// <summary>Always true; the section is readable.</summary>
    public override bool CanRead => true;

    /// <summary>Always true; the section is seekable.</summary>
    public override bool CanSeek => true;

    /// <summary>Always false; the section is read-only.</summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Position within the section. The getter clamps the underlying stream position to the
    /// section bounds (the base stream may have been seeked externally); the setter accepts
    /// only values in [0, Length] and throws an ArgumentOutOfRangeException otherwise.
    /// </summary>
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

    /// <summary>
    /// Reads up to count bytes at the current position, clamped to the section bounds.
    /// Returns 0 when the underlying position is outside the section.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="offset">Offset in the buffer at which to start writing.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <returns>The number of bytes read, or 0 at the end of the section.</returns>
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

    /// <summary>
    /// Reads up to buffer.Length bytes at the current position, clamped to the section bounds.
    /// Returns 0 when the underlying position is outside the section.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <returns>The number of bytes read, or 0 at the end of the section.</returns>
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

    /// <summary>Sets the position within the section from the given origin and returns it.</summary>
    /// <param name="offset">Offset relative to the origin.</param>
    /// <param name="origin">Reference point for the seek.</param>
    /// <returns>The new position within the section.</returns>
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

    /// <summary>No-op; the section is read-only.</summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported.</summary>
    /// <param name="value">The new length.</param>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported.</summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <param name="offset">Offset in the buffer at which to start reading.</param>
    /// <param name="count">Number of bytes to write.</param>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
