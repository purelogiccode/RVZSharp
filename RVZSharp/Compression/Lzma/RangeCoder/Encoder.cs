#nullable disable
namespace RVZSharp.Compression.Lzma.RangeCoder;

/// <summary>
/// Range encoder state machine kept for parity with the SDK sources; not exercised by the
/// RVZ decode path.
/// </summary>
internal class Encoder
{
    /// <summary>Range width below which a byte must be emitted and the interval shifted.</summary>
    public const uint KTopValue = (1 << 24);

    private Stream _stream;

    /// <summary>The low 64-bit part of the encoder interval.</summary>
    public ulong Low;

    /// <summary>The current width of the encoder interval.</summary>
    public uint Range;

    private uint _cacheSize;
    private byte _cache;

    /// <summary>Binds the stream the encoded bytes are written to.</summary>
    /// <param name="stream">The output stream.</param>
    public void SetStream(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Detaches the encoder from its output stream.</summary>
    public void ReleaseStream()
    {
        _stream = null;
    }

    /// <summary>Resets the encoder interval and cache state.</summary>
    public void Init()
    {
        //StartPosition = Stream.Position;
        Low = 0;
        Range = 0xFFFFFFFF;
        _cacheSize = 1;
        _cache = 0;
    }

    /// <summary>Emits the final five bytes that close the range-coded stream.</summary>
    public void FlushData()
    {
        for (var i = 0; i < 5; i++)
        {
            ShiftLow();
        }
    }

    /// <summary>Flushes the underlying output stream.</summary>
    public void FlushStream()
    {
        _stream.Flush();
    }

    /// <summary>Disposes the underlying output stream.</summary>
    public void CloseStream()
    {
        _stream.Dispose();
    }

    /// <summary>Emits pending cached bytes and shifts the interval left by one byte.</summary>
    public void ShiftLow()
    {
        if ((uint)Low < 0xFF000000 || (uint)(Low >> 32) == 1)
        {
            var temp = _cache;
            do
            {
                _stream.WriteByte((byte)(temp + (Low >> 32)));
                temp = 0xFF;
            } while (--_cacheSize != 0);

            _cache = (byte)(((uint)Low) >> 24);
        }

        _cacheSize++;
        Low = ((uint)Low) << 8;
    }

    /// <summary>Encodes <c>numTotalBits</c> raw (un-modeled) value bits, most significant bit first.</summary>
    /// <param name="v">The value whose bits are encoded.</param>
    /// <param name="numTotalBits">The number of bits to encode.</param>
    public void EncodeDirectBits(uint v, int numTotalBits)
    {
        for (var i = numTotalBits - 1; i >= 0; i--)
        {
            Range >>= 1;
            if (((v >> i) & 1) == 1)
            {
                Low += Range;
            }

            if (Range < KTopValue)
            {
                Range <<= 8;
                ShiftLow();
            }
        }
    }

    /// <summary>Not implemented in this port.</summary>
    /// <returns>Always -1.</returns>
    public long GetProcessedSizeAdd()
    {
        return -1;
    }
}