namespace RVZSharp.Compression.Lzma.RangeCoder;

/// <summary>Binary decision tree over a set of bit models, decoding multi-bit symbols by walking the tree.</summary>
internal readonly struct BitTreeDecoder
{
    private readonly BitDecoder[] _models;
    private readonly int _numBitLevels;

    /// <summary>Allocates the tree models.</summary>
    /// <param name="numBitLevels">Number of bit levels of the tree (2^numBitLevels leaves).</param>
    public BitTreeDecoder(int numBitLevels)
    {
        _numBitLevels = numBitLevels;
        _models = new BitDecoder[1 << numBitLevels];
    }

    /// <summary>Resets all tree models to neutral probabilities.</summary>
    public void Init()
    {
        for (uint i = 1; i < (1 << _numBitLevels); i++)
        {
            _models[i].Init();
        }
    }

    /// <summary>Decodes a symbol through the tree, bit by bit, most significant bit first.</summary>
    /// <param name="rangeDecoder">The range decoder the bits come from.</param>
    /// <returns>The decoded symbol index.</returns>
    public uint Decode(Decoder rangeDecoder)
    {
        uint m = 1;
        for (var bitIndex = _numBitLevels; bitIndex > 0; bitIndex--)
        {
            m = (m << 1) + _models[m].Decode(rangeDecoder);
        }

        return m - ((uint)1 << _numBitLevels);
    }

    /// <summary>Decodes a symbol through the tree, least significant bit first (used for distances).</summary>
    /// <param name="rangeDecoder">The range decoder the bits come from.</param>
    /// <returns>The decoded symbol.</returns>
    public uint ReverseDecode(Decoder rangeDecoder)
    {
        uint m = 1;
        uint symbol = 0;
        for (var bitIndex = 0; bitIndex < _numBitLevels; bitIndex++)
        {
            var bit = _models[m].Decode(rangeDecoder);
            m <<= 1;
            m += bit;
            symbol |= (bit << bitIndex);
        }

        return symbol;
    }

    /// <summary>Reverse-order tree decode over a flat array of models.</summary>
    /// <param name="models">The array hosting the tree models.</param>
    /// <param name="startIndex">Index of the first model of this tree within the array.</param>
    /// <param name="rangeDecoder">The range decoder the bits come from.</param>
    /// <param name="numBitLevels">Number of bit levels of the tree.</param>
    /// <returns>The decoded symbol.</returns>
    public static uint ReverseDecode(
        BitDecoder[] models,
        uint startIndex,
        Decoder rangeDecoder,
        int numBitLevels
    )
    {
        uint m = 1;
        uint symbol = 0;
        for (var bitIndex = 0; bitIndex < numBitLevels; bitIndex++)
        {
            var bit = models[startIndex + m].Decode(rangeDecoder);
            m <<= 1;
            m += bit;
            symbol |= (bit << bitIndex);
        }

        return symbol;
    }
}