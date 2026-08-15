#nullable disable
namespace RVZSharp.Compression.Lzma.RangeCoder;

internal class Encoder
{
    public const uint KTopValue = (1 << 24);

    private Stream _stream;

    public ulong Low;
    public uint Range;
    private uint _cacheSize;
    private byte _cache;

    public void SetStream(Stream stream)
    {
        _stream = stream;
    }

    public void ReleaseStream()
    {
        _stream = null;
    }

    public void Init()
    {
        //StartPosition = Stream.Position;
        Low = 0;
        Range = 0xFFFFFFFF;
        _cacheSize = 1;
        _cache = 0;
    }

    public void FlushData()
    {
        for (var i = 0; i < 5; i++)
        {
            ShiftLow();
        }
    }

    public void FlushStream()
    {
        _stream.Flush();
    }

    public void CloseStream()
    {
        _stream.Dispose();
    }

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

    public long GetProcessedSizeAdd()
    {
        return -1;
    }
}