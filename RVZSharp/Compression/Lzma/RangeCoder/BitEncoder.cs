namespace RVZSharp.Compression.Lzma.RangeCoder;

/// <summary>Adaptive binary probability model for range encoding, including price estimation.</summary>
internal struct BitEncoder
{
    /// <summary>Number of fraction bits of the bit-model probability scale.</summary>
    public const int KNumBitModelTotalBits = 11;

    /// <summary>Total probability scale of a bit model.</summary>
    public const uint KBitModelTotal = (1 << KNumBitModelTotalBits);

    private const int KNumMoveBits = 5;
    private const int KNumMoveReducingBits = 2;

    /// <summary>Scale bits of probability prices.</summary>
    public const int KNumBitPriceShiftBits = 6;

    private uint _prob;

    /// <summary>Resets the probability to the neutral midpoint of the scale.</summary>
    public void Init()
    {
        _prob = KBitModelTotal >> 1;
    }

    /// <summary>Adapts the model probability toward the observed symbol.</summary>
    /// <param name="symbol">The bit that was observed.</param>
    public void UpdateModel(uint symbol)
    {
        if (symbol == 0)
        {
            _prob += (KBitModelTotal - _prob) >> KNumMoveBits;
        }
        else
        {
            _prob -= (_prob) >> KNumMoveBits;
        }
    }

    /// <summary>Encodes one bit with this model into the range encoder.</summary>
    /// <param name="encoder">The range encoder receiving the bit.</param>
    /// <param name="symbol">The bit to encode (0 or 1).</param>
    public void Encode(Encoder encoder, uint symbol)
    {
        var newBound = (encoder.Range >> KNumBitModelTotalBits) * _prob;
        if (symbol == 0)
        {
            encoder.Range = newBound;
            _prob += (KBitModelTotal - _prob) >> KNumMoveBits;
        }
        else
        {
            encoder.Low += newBound;
            encoder.Range -= newBound;
            _prob -= (_prob) >> KNumMoveBits;
        }

        if (encoder.Range < Encoder.KTopValue)
        {
            encoder.Range <<= 8;
            encoder.ShiftLow();
        }
    }

    private static readonly uint[] ProbPrices = new uint[
        KBitModelTotal >> KNumMoveReducingBits
    ];

    static BitEncoder()
    {
        const int kNumBits = (KNumBitModelTotalBits - KNumMoveReducingBits);
        for (var i = kNumBits - 1; i >= 0; i--)
        {
            var start = (uint)1 << (kNumBits - i - 1);
            var end = (uint)1 << (kNumBits - i);
            for (var j = start; j < end; j++)
            {
                ProbPrices[j] =
                    ((uint)i << KNumBitPriceShiftBits)
                    + (((end - j) << KNumBitPriceShiftBits) >> (kNumBits - i - 1));
            }
        }
    }

    /// <summary>Returns the estimated bit cost of encoding the given symbol using the current probability.</summary>
    /// <param name="symbol">The symbol (0 or 1) whose price is requested.</param>
    /// <returns>The approximate cost in price-scale units.</returns>
    public readonly uint GetPrice(uint symbol)
    {
        return ProbPrices[
            (((_prob - symbol) ^ ((-(int)symbol))) & (KBitModelTotal - 1))
            >> KNumMoveReducingBits
        ];
    }

    /// <summary>Returns the estimated cost of encoding symbol 0.</summary>
    /// <returns>The approximate cost in price-scale units.</returns>
    public readonly uint GetPrice0()
    {
        return ProbPrices[_prob >> KNumMoveReducingBits];
    }

    /// <summary>Returns the estimated cost of encoding symbol 1.</summary>
    /// <returns>The approximate cost in price-scale units.</returns>
    public readonly uint GetPrice1()
    {
        return ProbPrices[(KBitModelTotal - _prob) >> KNumMoveReducingBits];
    }
}