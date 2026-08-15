namespace RVZSharp.Packing;

/// <summary>
/// Decodes the RVZ packing scheme: a sequence of 4-byte big-endian size fields; a clear
/// MSB means the next <c>size</c> bytes are literal data, a set MSB means the next 68 bytes
/// are a PRNG seed and <c>size</c> bytes of generated padding follow (with the PRNG state
/// advanced by <c>dataOffset % 0x8000</c> before the first byte, per the format spec).
/// </summary>
public sealed class RvzPackingDecoder : Stream
{
    private const uint PaddedFlag = 0x80000000;
    private const uint SizeMask = PaddedFlag - 1;

    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly long _dataOffset;
    private long _emitted;
    private readonly LaggedFibonacciPrng _prng = new();
    private readonly byte[] _buffer = new byte[LaggedFibonacciPrng.BufferSize];
    private int _bufferStart;
    private int _bufferEnd;
    private bool _endReached;
    private bool _isPaddedSegment;
    private uint _segmentRemaining;

    /// <summary>
    /// Wraps a packed segment stream. <c>dataOffset</c> is the disc-relative offset of the
    /// packed data, used to position the PRNG state for each segment.
    /// </summary>
    /// <param name="input">The packed segment stream (size headers, literals and seeds).</param>
    /// <param name="dataOffset">Offset of this packed data within its area, used for the PRNG skip.</param>
    /// <param name="leaveOpen">True to keep <c>input</c> open on dispose.</param>
    public RvzPackingDecoder(Stream input, long dataOffset, bool leaveOpen = false)
    {
        _input = input;
        _dataOffset = dataOffset;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Always true: the stream only supports reading.</summary>
    public override bool CanRead => true;

    /// <summary>Always false: the packed stream is forward-only.</summary>
    public override bool CanSeek => false;

    /// <summary>Always false: the packed stream is read-only.</summary>
    public override bool CanWrite => false;

    /// <summary>The length is unknown until the stream is fully decoded.</summary>
    public override long Length => throw new NotSupportedException();

    /// <summary>Position is not tracked; throws for both accessors.</summary>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Reads decoded bytes into <c>buffer</c>, decompressing literal and PRNG-padded
    /// segments on demand.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="offset">Offset into <c>buffer</c>.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <returns>The number of bytes read; 0 at the clean end of the packed stream.</returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            if (!EnsureBuffered())
            {
                break;
            }

            var available = _bufferEnd - _bufferStart;
            var take = Math.Min(count - total, available);
            Array.Copy(_buffer, _bufferStart, buffer, offset + total, take);
            _bufferStart += take;
            _segmentRemaining -= (uint)take;
            _emitted += take;
            total += take;
        }

        return total;
    }

    /// <summary>
    /// Makes sure the output buffer holds data: refills from the current segment, or starts
    /// the next segment when the current one is exhausted. Returns false at a clean end of
    /// the packed stream (always at a segment boundary).
    /// </summary>
    private bool EnsureBuffered()
    {
        while (_bufferStart == _bufferEnd)
        {
            if (_segmentRemaining == 0)
            {
                if (!ReadSegmentHeader())
                {
                    return false;
                }

                if (_segmentRemaining == 0)
                {
                    continue; // zero-length segment
                }
            }

            if (_isPaddedSegment)
            {
                FillFromPrng();
            }
            else
            {
                FillFromInput();
            }
        }

        return true;
    }

    private bool ReadSegmentHeader()
    {
        if (_endReached)
        {
            return false;
        }

        // A partial header at EOF is corruption, not a clean end: only a true end-of-input
        // at a segment boundary terminates the stream (Dolphin fails, Go returns
        // ErrUnexpectedEOF).
        var sizeBytes = new byte[4];
        var read = 0;
        while (read < sizeBytes.Length)
        {
            var n = _input.Read(sizeBytes, read, sizeBytes.Length - read);
            if (n <= 0)
            {
                if (read == 0)
                {
                    _endReached = true;
                    return false;
                }

                throw new RvzFormatException(
                    "Truncated RVZ packing: partial segment size header.");
            }

            read += n;
        }

        var size = (uint)((sizeBytes[0] << 24) | (sizeBytes[1] << 16) | (sizeBytes[2] << 8) | sizeBytes[3]);
        _isPaddedSegment = (size & PaddedFlag) != 0;
        _segmentRemaining = size & SizeMask;

        if (_isPaddedSegment)
        {
            var seed = new byte[LaggedFibonacciPrng.SeedSize];
            if (!ReadExactly(_input, seed))
            {
                throw new RvzFormatException("Truncated RVZ packing: expected a PRNG seed.");
            }

            _prng.SetSeed(seed);
            // Skip to the PRNG position of this segment: the writer recovers the seed for
            // the running data offset (chunk start + bytes already emitted in this chunk).
            _prng.Forward((int)((_dataOffset + _emitted) % 0x8000));
        }

        return true;
    }

    private void FillFromPrng()
    {
        _bufferStart = 0;
        var take = (int)Math.Min(_segmentRemaining, (uint)_buffer.Length);
        _prng.GetBytes(_buffer, take);
        _bufferEnd = take;
    }

    private void FillFromInput()
    {
        _bufferStart = 0;
        var take = (int)Math.Min(_segmentRemaining, (uint)_buffer.Length);
        if (!ReadExactly(_input, _buffer.AsSpan(0, take)))
        {
            throw new RvzFormatException("Truncated RVZ packing: literal segment is shorter than declared.");
        }

        _bufferEnd = take;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _input.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>No-op: the stream is read-only.</summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported; the packed stream cannot be seeked.</summary>
    /// <param name="offset">Ignored.</param>
    /// <param name="origin">Ignored.</param>
    /// <returns>Never returns.</returns>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported; the packed stream is read-only.</summary>
    /// <param name="value">Ignored.</param>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported; the packed stream is read-only.</summary>
    /// <param name="buffer">Ignored.</param>
    /// <param name="offset">Ignored.</param>
    /// <param name="count">Ignored.</param>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
