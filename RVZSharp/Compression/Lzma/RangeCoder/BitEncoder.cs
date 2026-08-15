namespace RVZSharp.Compression.Lzma.RangeCoder;

internal struct BitEncoder
{
    public const int KNumBitModelTotalBits = 11;
    public const uint KBitModelTotal = (1 << KNumBitModelTotalBits);
    private const int KNumMoveBits = 5;
    private const int KNumMoveReducingBits = 2;
    public const int KNumBitPriceShiftBits = 6;

    private uint _prob;

    public void Init()
    {
        _prob = KBitModelTotal >> 1;
    }

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

    public readonly uint GetPrice(uint symbol)
    {
        return ProbPrices[
            (((_prob - symbol) ^ ((-(int)symbol))) & (KBitModelTotal - 1))
            >> KNumMoveReducingBits
        ];
    }

    public readonly uint GetPrice0()
    {
        return ProbPrices[_prob >> KNumMoveReducingBits];
    }

    public readonly uint GetPrice1()
    {
        return ProbPrices[(KBitModelTotal - _prob) >> KNumMoveReducingBits];
    }
}