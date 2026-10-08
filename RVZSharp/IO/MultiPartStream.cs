namespace RVZSharp.IO;

/// <summary>
/// Concatenates the parts of a split image (WBFS <c>game.wbfs</c> + <c>game.wbf1</c> + …,
/// plain ISO <c>game.part0.iso</c> + <c>game.part1.iso</c> + …) into one seekable stream.
/// Owns the continuation parts; the first part follows the caller's <c>leaveOpen</c> choice
/// (Dolphin: WbfsFileReader and SplitPlainFileReader).
/// </summary>
internal sealed class MultiPartStream : Stream
{
    private readonly Stream[] _parts;
    private readonly long[] _starts;
    private readonly bool _leaveOpen;

    /// <summary>Creates the concatenation of <paramref name="parts"/> (in order).</summary>
    /// <param name="parts">The parts; the first one follows <paramref name="leaveOpen"/>.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves the first part open.</param>
    public MultiPartStream(List<Stream> parts, bool leaveOpen)
    {
        _parts = parts.ToArray();
        _leaveOpen = leaveOpen;
        _starts = new long[parts.Count];
        var running = 0L;
        for (var i = 0; i < parts.Count; i++)
        {
            _starts[i] = running;
            running += parts[i].Length;
        }

        Length = running;
    }

    /// <summary>True; the parts are readable.</summary>
    public override bool CanRead => true;

    /// <summary>True; the concatenation is seekable.</summary>
    public override bool CanSeek => true;

    /// <summary>False; split images are read-only.</summary>
    public override bool CanWrite => false;

    /// <summary>Total size of all parts in bytes.</summary>
    public override long Length { get; }

    /// <summary>Current position in the concatenated image.</summary>
    public override long Position { get; set; }

    /// <summary>Reads bytes across part boundaries.</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>Reads bytes across part boundaries.</summary>
    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length && Position < Length)
        {
            var partIndex = Array.BinarySearch(_starts, Position);
            if (partIndex < 0)
            {
                partIndex = ~partIndex - 1;
            }

            var part = _parts[partIndex];
            var local = Position - _starts[partIndex];
            if (part.Position != local)
            {
                part.Position = local;
            }

            var take = (int)Math.Min(buffer.Length - total, part.Length - local);
            var read = part.Read(buffer.Slice(total, take));
            if (read <= 0)
            {
                break;
            }

            total += read;
            Position += read;
        }

        return total;
    }

    /// <summary>Moves the position within the concatenated image.</summary>
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

    /// <summary>No-op; reads always go to the parts.</summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported; split images are read-only.</summary>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported; split images are read-only.</summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>Disposes the continuation parts, and the first part unless leaveOpen was set.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            for (var i = _leaveOpen ? 1 : 0; i < _parts.Length; i++)
            {
                _parts[i].Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
