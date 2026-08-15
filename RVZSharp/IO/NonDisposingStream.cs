namespace RVZSharp.IO;

/// <summary>Wraps a stream so that disposing the wrapper does not dispose the underlying stream.</summary>
public sealed class NonDisposingStream : Stream
{
    private readonly Stream _inner;

    /// <summary>Wraps a stream without taking ownership of it.</summary>
    /// <param name="inner">The stream to wrap; it will not be disposed by this wrapper.</param>
    public NonDisposingStream(Stream inner)
    {
        _inner = inner;
    }

    /// <summary>Whether the wrapped stream supports reading.</summary>
    public override bool CanRead => _inner.CanRead;

    /// <summary>Whether the wrapped stream supports seeking.</summary>
    public override bool CanSeek => _inner.CanSeek;

    /// <summary>Always false; writing through this wrapper is not supported.</summary>
    public override bool CanWrite => false;

    /// <summary>Delegates to the wrapped stream.</summary>
    public override long Length => _inner.Length;

    /// <summary>Delegates to the wrapped stream.</summary>
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>Delegates to the wrapped stream.</summary>
    public override void Flush()
    {
        _inner.Flush();
    }

    /// <summary>Reads bytes from the wrapped stream.</summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="offset">Offset in the buffer at which to start writing.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <returns>The number of bytes read, or 0 at the end of the stream.</returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, count);
    }

    /// <summary>Reads bytes from the wrapped stream.</summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <returns>The number of bytes read, or 0 at the end of the stream.</returns>
    public override int Read(Span<byte> buffer)
    {
        return _inner.Read(buffer);
    }

    /// <summary>Delegates to the wrapped stream.</summary>
    /// <param name="offset">Offset relative to the origin.</param>
    /// <param name="origin">Reference point for the seek.</param>
    /// <returns>The new position in the stream.</returns>
    public override long Seek(long offset, SeekOrigin origin)
    {
        return _inner.Seek(offset, origin);
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
