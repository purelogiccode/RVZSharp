#nullable disable

using System.Buffers;
using System.Runtime.CompilerServices;

namespace RVZSharp.Compression.Lzma.RangeCoder;

/// <summary>
/// Range decoder: maintains the Code/Range interval state and pulls bytes from the
/// compressed stream so probability-coded and direct bits can be extracted.
/// </summary>
internal class Decoder
{
    /// <summary>Interval width below which the code state must be re-normalized.</summary>
    public const uint K_TOP_VALUE = (1 << 24);

    /// <summary>Current range interval width (raw storage for the Range property).</summary>
    public uint Range2;

    /// <summary>Current code value inside the interval (raw storage for the Code property).</summary>
    public uint Code2;

    /// <summary>Gets or sets the current interval width.</summary>
    public uint Range
    {
        get => Range2;
        set => Range2 = value;
    }

    /// <summary>Gets or sets the code value inside the current interval.</summary>
    public uint Code
    {
        get => Code2;
        set => Code2 = value;
    }

    /// <summary>The compressed input stream the decoder pulls bytes from.</summary>
    public Stream Stream;

    /// <summary>Total number of compressed bytes consumed so far.</summary>
    public long Total;

    // Upper bound (in terms of _total) that the fast buffered reader is allowed to physically
    // read up to. -1 means unbounded. This matters for formats like LZMA2 where multiple
    // independent chunks share one underlying stream: chunk headers are read directly from that
    // stream between decode sessions, so the fast path must not read past the end of the
    // current chunk's compressed data, or it would desynchronize the stream position for the
    // next chunk header read. Set via SetFastLimit once the caller knows the chunk/stream size.
    private long _fastLimit = -1;

    // Whether it is safe to bulk-read ahead of the decoder even without a known _fastLimit.
    // This is only true for streams that are guaranteed to self-clamp Read() to their own
    // logical end and never return bytes that belong to something else - e.g. 7Zip's
    // per-folder BufferedSubStream, which limits every Read() to its own remaining pack size.
    // It is false for everything else, notably a shared streaming Zip reader stream with an
    // unknown compressed size (data-descriptor entries): that stream keeps handing out bytes
    // past the logical end of this LZMA stream (the next entry's header, etc.) with no self
    // clamping and no way to give unread bytes back, so bulk-buffering there would
    // desynchronize the stream. Note a stream reporting a queryable Length is NOT a reliable
    // signal here: some wrapper streams (e.g. SharpCompressStream's ring-buffer mode used for
    // over-read recording on non-seekable Zip streams) expose a Length without actually
    // bounding Read() to the current logical stream's end. In the unsafe case we fall back to
    // reading exactly one byte at a time, matching a plain per-byte read of the stream.
    private bool _fastBufferSafeUnbounded;

    /// <summary>Sets the byte limit of the fast buffered reader (in total bytes); -1 means unbounded.</summary>
    /// <param name="limit">The maximum total number of bytes to read from the stream.</param>
    public void SetFastLimit(long limit)
    {
        _fastLimit = limit;
    }

    /// <summary>Starts a new decode session: reads the initial 5 code bytes and resets interval state.</summary>
    /// <param name="stream">The compressed input stream.</param>
    public void Init(Stream stream)
    {
        Stream = stream;

        Code2 = 0;
        Range2 = 0xFFFFFFFF;
        for (var i = 0; i < 5; i++)
        {
            Code2 = (Code2 << 8) | (byte)Stream.ReadByte();
        }

        Total = 5;
        _fastLimit = -1;
        FastBufferPos = 0;
        FastBufferLen = 0;
        _fastEndOfStream = false;
        _fastBufferSafeUnbounded = false;
    }

    /// <summary>Returns the fast read buffer to the pool, drops the buffered data and detaches the stream.</summary>
    public void ReleaseStream()
    {
        ReleaseFastBuffer();
        Stream = null;
    }

    private const int FastBufferSize = 1 << 16;
    private byte[] _fastBuffer;
    private bool _fastEndOfStream;

    /// <summary>Rented, lazily-created backing array of the fast read-ahead buffer.</summary>
    internal byte[] FastBufferArray => _fastBuffer ??= ArrayPool<byte>.Shared.Rent(FastBufferSize);

    /// <summary>Read position of the caller inside the fast input buffer.</summary>
    internal int FastBufferPos { get; set; }

    /// <summary>Number of valid bytes currently in the fast input buffer.</summary>
    internal int FastBufferLen { get; private set; }

    /// <summary>Adds batch-consumed bytes to the total consumed count.</summary>
    /// <param name="consumed">The number of bytes consumed since the last batch.</param>
    internal void AddTotal(long consumed)
    {
        Total += consumed;
    }

    /// <summary>Refills the fast input buffer from the underlying stream.</summary>
    internal void RefillFast()
    {
        FillFastBuffer();
    }

    private void FillFastBuffer()
    {
        _fastBuffer ??= ArrayPool<byte>.Shared.Rent(FastBufferSize);
        if (_fastEndOfStream)
        {
            FastBufferPos = 0;
            FastBufferLen = 1;
            _fastBuffer[0] = 0xFF;
            return;
        }

        var requestSize = _fastBuffer.Length;
        if (_fastLimit >= 0)
        {
            var remaining = _fastLimit - Total;
            requestSize = remaining <= 0 ? 1 : (int)Math.Min(requestSize, remaining);
        }
        else if (!_fastBufferSafeUnbounded)
        {
            requestSize = 1;
        }

        var read = Stream.Read(_fastBuffer, 0, requestSize);
        if (read <= 0)
        {
            _fastEndOfStream = true;
            FastBufferPos = 0;
            FastBufferLen = 1;
            _fastBuffer[0] = 0xFF;
            return;
        }

        FastBufferPos = 0;
        FastBufferLen = read;
    }

    private void ReleaseFastBuffer()
    {
        if (_fastBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_fastBuffer);
            _fastBuffer = null;
        }

        FastBufferPos = 0;
        FastBufferLen = 0;
        _fastEndOfStream = false;
    }

    /// <summary>Whether the range decoder reached its end-of-stream state (Code == 0).</summary>
    /// <returns>True when the stream is fully consumed.</returns>
    public bool IsFinished => Code2 == 0;

    /// <summary>Shifts interval and code left while the range is below the top value, pulling bytes from the stream.</summary>
    public void Normalize()
    {
        while (Range2 < K_TOP_VALUE)
        {
            Code2 = (Code2 << 8) | (byte)Stream.ReadByte();
            Range2 <<= 8;
            Total++;
        }
    }

    /// <summary>Single-step variant of Normalize: performs at most one shift/refill.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Normalize2()
    {
        if (Range2 < K_TOP_VALUE)
        {
            Code2 = (Code2 << 8) | (byte)Stream.ReadByte();
            Range2 <<= 8;
            Total++;
        }
    }

    /// <summary>Divides the range by the model total and returns the position of the code within it.</summary>
    /// <param name="total">The total probability scale of the model.</param>
    /// <returns>The threshold index containing the current code.</returns>
    public uint GetThreshold(uint total)
    {
        return Code2 / (Range2 /= total);
    }

    /// <summary>Narrows the interval to the chosen model region [start, start + size).</summary>
    /// <param name="start">Start of the chosen region in divided-range units.</param>
    /// <param name="size">Width of the chosen region in divided-range units.</param>
    public void Decode(uint start, uint size)
    {
        Code2 -= start * Range2;
        Range2 *= size;
        Normalize();
    }

    /// <summary>Decodes <c>numTotalBits</c> raw (un-modeled) value bits directly from the stream.</summary>
    /// <param name="numTotalBits">The number of bits to decode.</param>
    /// <returns>The decoded value.</returns>
    public uint DecodeDirectBits(int numTotalBits)
    {
        var range = Range2;
        var code = Code2;
        uint result = 0;
        for (var i = numTotalBits; i > 0; i--)
        {
            range >>= 1;
            var t = (code - range) >> 31;
            code -= range & (t - 1);
            result = (result << 1) | (1 - t);

            if (range < K_TOP_VALUE)
            {
                code = (code << 8) | (byte)Stream.ReadByte();
                range <<= 8;
                Total++;
            }
        }

        Range2 = range;
        Code2 = code;
        return result;
    }

    /// <summary>Decodes a single bit with a model whose zero-symbol probability is <c>size0</c>/2^<c>numTotalBits</c>.</summary>
    /// <param name="size0">The size of the 0-symbol region of the model.</param>
    /// <param name="numTotalBits">Total number of bits in the model scale.</param>
    /// <returns>The decoded bit (0 or 1).</returns>
    public uint DecodeBit(uint size0, int numTotalBits)
    {
        var newBound = (Range2 >> numTotalBits) * size0;
        uint symbol;
        if (Code2 < newBound)
        {
            symbol = 0;
            Range2 = newBound;
        }
        else
        {
            symbol = 1;
            Code2 -= newBound;
            Range2 -= newBound;
        }

        Normalize();
        return symbol;
    }
}