namespace RVZSharp.Compression.Lzma.RangeCoder;

internal struct BitDecoder
{
    public const int K_NUM_BIT_MODEL_TOTAL_BITS = 11;
    public const uint K_BIT_MODEL_TOTAL = (1 << K_NUM_BIT_MODEL_TOTAL_BITS);
    private const int KNumMoveBits = 5;

    private uint _prob;

    public void Init()
    {
        _prob = K_BIT_MODEL_TOTAL >> 1;
    }

    public uint Decode(Decoder rangeDecoder)
    {
        var newBound = (rangeDecoder.Range >> K_NUM_BIT_MODEL_TOTAL_BITS) * _prob;
        if (rangeDecoder.Code < newBound)
        {
            rangeDecoder.Range = newBound;
            _prob += (K_BIT_MODEL_TOTAL - _prob) >> KNumMoveBits;
            rangeDecoder.Normalize2();
            return 0;
        }
        rangeDecoder.Range -= newBound;
        rangeDecoder.Code -= newBound;
        _prob -= (_prob) >> KNumMoveBits;
        rangeDecoder.Normalize2();
        return 1;
    }
}