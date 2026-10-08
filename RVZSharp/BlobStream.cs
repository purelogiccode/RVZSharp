using RVZSharp.Interfaces;

namespace RVZSharp;

/// <summary>
/// Exposes any <see cref="IBlobReader"/> as a read-only, seekable <see cref="Stream"/>, so
/// consumers can use standard stream APIs (<c>BinaryReader</c>, <c>Stream.CopyTo</c>,
/// serializers) directly on a decoded disc image of any size.
/// </summary>
public sealed class BlobStream : Stream
{
    private readonly bool _leaveOpen;

    /// <summary>Wraps <paramref name="reader"/> in a seekable stream.</summary>
    /// <param name="reader">The blob to expose.</param>
    /// <param name="leaveOpen">
    /// Whether disposing this stream leaves <paramref name="reader"/> open.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public BlobStream(IBlobReader reader, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Blob = reader;
        _leaveOpen = leaveOpen;
    }

    /// <summary>The wrapped blob.</summary>
    public IBlobReader Blob { get; }

    /// <summary>True; the decoded image is readable.</summary>
    public override bool CanRead => true;

    /// <summary>True; the decoded image is randomly accessible.</summary>
    public override bool CanSeek => true;

    /// <summary>False; decoded images are read-only.</summary>
    public override bool CanWrite => false;

    /// <summary>Size of the decoded disc image in bytes.</summary>
    public override long Length => Blob.Length;

    /// <summary>Current position in the decoded disc image.</summary>
    public override long Position { get; set; }

    /// <summary>Reads decoded disc bytes at the current position.</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>Reads decoded disc bytes at the current position.</summary>
    public override int Read(Span<byte> buffer)
    {
        var read = Blob.ReadAt(Position, buffer);
        Position += read;
        return read;
    }

    /// <summary>Moves the position within the decoded disc image.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => Length + offset
        };
        return Position;
    }

    /// <summary>No-op; decoded images have no write buffer.</summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported; decoded images are read-only.</summary>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported; decoded images are read-only.</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>Disposes the wrapped reader unless leaveOpen was set.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            Blob.Dispose();
        }

        base.Dispose(disposing);
    }
}
