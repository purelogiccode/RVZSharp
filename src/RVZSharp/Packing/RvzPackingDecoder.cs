using RVZSharp.IO;

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

    public RvzPackingDecoder(Stream input, long dataOffset, bool leaveOpen = false)
    {
        _input = input;
        _dataOffset = dataOffset;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

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

        var sizeBytes = new byte[4];
        if (!ReadExactly(_input, sizeBytes))
        {
            _endReached = true;
            return false;
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

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
