namespace RVZSharp.Compression.Lzma.RangeCoder;

/// <summary>Adaptive binary probability model used by the range decoder to decode one bit.</summary>
internal struct BitDecoder
{
    /// <summary>Number of fraction bits of the bit-model probability scale.</summary>
    public const int K_NUM_BIT_MODEL_TOTAL_BITS = 11;

    /// <summary>Total probability scale of a bit model.</summary>
    public const uint K_BIT_MODEL_TOTAL = (1 << K_NUM_BIT_MODEL_TOTAL_BITS);

    private const int KNumMoveBits = 5;

    private uint _prob;

    /// <summary>Resets the probability to the neutral midpoint of the scale.</summary>
    public void Init()
    {
        _prob = K_BIT_MODEL_TOTAL >> 1;
    }

    /// <summary>Decodes one bit with this model and adapts the probability toward the outcome.</summary>
    /// <param name="rangeDecoder">The range decoder the bit is drawn from.</param>
    /// <returns>The decoded bit (0 or 1).</returns>
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