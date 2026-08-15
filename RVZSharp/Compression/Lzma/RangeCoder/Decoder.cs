#nullable disable

using System.Runtime.CompilerServices;
#if !LEGACY_DOTNET
using System.Buffers;
#endif

namespace RVZSharp.Compression.Lzma.RangeCoder;

internal class Decoder
{
    public const uint K_TOP_VALUE = (1 << 24);
    public uint _range;
    public uint _code;

    public uint Range { get => _range; set => _range = value; }
    public uint Code { get => _code; set => _code = value; }

    public Stream _stream;
    public long _total;

#if !LEGACY_DOTNET
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
    // reading exactly one byte at a time, matching the legacy per-byte ReadByte() behavior.
    private bool _fastBufferSafeUnbounded;

    public void SetFastLimit(long limit)
    {
        _fastLimit = limit;
    }
#endif

    public void Init(Stream stream)
    {
        _stream = stream;

        _code = 0;
        _range = 0xFFFFFFFF;
        for (var i = 0; i < 5; i++)
        {
            _code = (_code << 8) | (byte)_stream.ReadByte();
        }
        _total = 5;
#if !LEGACY_DOTNET
        _fastLimit = -1;
        FastBufferPos = 0;
        _fastBufferLen = 0;
        _fastEndOfStream = false;
        _fastBufferSafeUnbounded = false;
#endif
    }

    public void ReleaseStream()
    {
#if !LEGACY_DOTNET
        ReleaseFastBuffer();
#endif
        _stream = null;
    }

#if !LEGACY_DOTNET
    private const int FastBufferSize = 1 << 16;
    private byte[] _fastBuffer;
    private int _fastBufferLen;
    private bool _fastEndOfStream;

    internal byte[] FastBufferArray => _fastBuffer ??= ArrayPool<byte>.Shared.Rent(FastBufferSize);

    internal int FastBufferPos { get; set; }

    internal int FastBufferLen => _fastBufferLen;

    internal void AddTotal(long consumed)
    {
        _total += consumed;
    }

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
            _fastBufferLen = 1;
            _fastBuffer[0] = 0xFF;
            return;
        }
        var requestSize = _fastBuffer.Length;
        if (_fastLimit >= 0)
        {
            var remaining = _fastLimit - _total;
            requestSize = remaining <= 0 ? 1 : (int)Math.Min(requestSize, remaining);
        }
        else if (!_fastBufferSafeUnbounded)
        {
            requestSize = 1;
        }
        var read = _stream.Read(_fastBuffer, 0, requestSize);
        if (read <= 0)
        {
            _fastEndOfStream = true;
            FastBufferPos = 0;
            _fastBufferLen = 1;
            _fastBuffer[0] = 0xFF;
            return;
        }
        FastBufferPos = 0;
        _fastBufferLen = read;
    }

    private void ReleaseFastBuffer()
    {
        if (_fastBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_fastBuffer);
            _fastBuffer = null;
        }
        FastBufferPos = 0;
        _fastBufferLen = 0;
        _fastEndOfStream = false;
    }
#endif

    public void Normalize()
    {
        while (_range < K_TOP_VALUE)
        {
            _code = (_code << 8) | (byte)_stream.ReadByte();
            _range <<= 8;
            _total++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Normalize2()
    {
        if (_range < K_TOP_VALUE)
        {
            _code = (_code << 8) | (byte)_stream.ReadByte();
            _range <<= 8;
            _total++;
        }
    }

    public uint GetThreshold(uint total)
    {
        return _code / (_range /= total);
    }

    public void Decode(uint start, uint size)
    {
        _code -= start * _range;
        _range *= size;
        Normalize();
    }

    public uint DecodeDirectBits(int numTotalBits)
    {
        var range = _range;
        var code = _code;
        uint result = 0;
        for (var i = numTotalBits; i > 0; i--)
        {
            range >>= 1;
            var t = (code - range) >> 31;
            code -= range & (t - 1);
            result = (result << 1) | (1 - t);

            if (range < K_TOP_VALUE)
            {
                code = (code << 8) | (byte)_stream.ReadByte();
                range <<= 8;
                _total++;
            }
        }
        _range = range;
        _code = code;
        return result;
    }

    public uint DecodeBit(uint size0, int numTotalBits)
    {
        var newBound = (_range >> numTotalBits) * size0;
        uint symbol;
        if (_code < newBound)
        {
            symbol = 0;
            _range = newBound;
        }
        else
        {
            symbol = 1;
            _code -= newBound;
            _range -= newBound;
        }
        Normalize();
        return symbol;
    }

    public bool IsFinished => _code == 0;
}