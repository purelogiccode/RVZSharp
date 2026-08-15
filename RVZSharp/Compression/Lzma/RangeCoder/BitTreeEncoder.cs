namespace RVZSharp.Compression.Lzma.RangeCoder;

/// <summary>Encoder-side decision tree over a set of bit models (used for prices in the SDK sources).</summary>
internal readonly struct BitTreeEncoder
{
    private readonly BitEncoder[] _models;
    private readonly int _numBitLevels;

    /// <summary>Allocates the tree models.</summary>
    /// <param name="numBitLevels">Number of bit levels of the tree.</param>
    public BitTreeEncoder(int numBitLevels)
    {
        _numBitLevels = numBitLevels;
        _models = new BitEncoder[1 << numBitLevels];
    }

    /// <summary>Resets all tree models to neutral probabilities.</summary>
    public void Init()
    {
        for (uint i = 1; i < (1 << _numBitLevels); i++)
        {
            _models[i].Init();
        }
    }

    /// <summary>Encodes a symbol through the tree, most significant bit first.</summary>
    /// <param name="rangeEncoder">The range encoder receiving the bits.</param>
    /// <param name="symbol">The symbol to encode.</param>
    public void Encode(Encoder rangeEncoder, uint symbol)
    {
        uint m = 1;
        for (var bitIndex = _numBitLevels; bitIndex > 0;)
        {
            bitIndex--;
            var bit = (symbol >> bitIndex) & 1;
            _models[m].Encode(rangeEncoder, bit);
            m = (m << 1) | bit;
        }
    }

    /// <summary>Encodes a symbol through the tree, least significant bit first.</summary>
    /// <param name="rangeEncoder">The range encoder receiving the bits.</param>
    /// <param name="symbol">The symbol to encode.</param>
    public void ReverseEncode(Encoder rangeEncoder, uint symbol)
    {
        uint m = 1;
        for (uint i = 0; i < _numBitLevels; i++)
        {
            var bit = symbol & 1;
            _models[m].Encode(rangeEncoder, bit);
            m = (m << 1) | bit;
            symbol >>= 1;
        }
    }

    /// <summary>Computes the price of encoding the given symbol, most significant bit first.</summary>
    /// <param name="symbol">The symbol whose price is requested.</param>
    /// <returns>The accumulated price in price-scale units.</returns>
    public uint GetPrice(uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (var bitIndex = _numBitLevels; bitIndex > 0;)
        {
            bitIndex--;
            var bit = (symbol >> bitIndex) & 1;
            price += _models[m].GetPrice(bit);
            m = (m << 1) + bit;
        }

        return price;
    }

    /// <summary>Computes the price of encoding the symbol in reverse bit order.</summary>
    /// <param name="symbol">The symbol whose price is requested.</param>
    /// <returns>The accumulated price in price-scale units.</returns>
    public uint ReverseGetPrice(uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (var i = _numBitLevels; i > 0; i--)
        {
            var bit = symbol & 1;
            symbol >>= 1;
            price += _models[m].GetPrice(bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    /// <summary>Reverse-order price computation over a flat array of models.</summary>
    /// <param name="models">The array hosting the tree models.</param>
    /// <param name="startIndex">Index of the first model of this tree within the array.</param>
    /// <param name="numBitLevels">Number of bit levels of the tree.</param>
    /// <param name="symbol">The symbol whose price is requested.</param>
    /// <returns>The accumulated price in price-scale units.</returns>
    public static uint ReverseGetPrice(
        BitEncoder[] models,
        uint startIndex,
        int numBitLevels,
        uint symbol
    )
    {
        uint price = 0;
        uint m = 1;
        for (var i = numBitLevels; i > 0; i--)
        {
            var bit = symbol & 1;
            symbol >>= 1;
            price += models[startIndex + m].GetPrice(bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    /// <summary>Reverse-order tree encode over a flat array of models.</summary>
    /// <param name="models">The array hosting the tree models.</param>
    /// <param name="startIndex">Index of the first model of this tree within the array.</param>
    /// <param name="rangeEncoder">The range encoder receiving the bits.</param>
    /// <param name="numBitLevels">Number of bit levels of the tree.</param>
    /// <param name="symbol">The symbol to encode.</param>
    public static void ReverseEncode(
        BitEncoder[] models,
        uint startIndex,
        Encoder rangeEncoder,
        int numBitLevels,
        uint symbol
    )
    {
        uint m = 1;
        for (var i = 0; i < numBitLevels; i++)
        {
            var bit = symbol & 1;
            models[startIndex + m].Encode(rangeEncoder, bit);
            m = (m << 1) | bit;
            symbol >>= 1;
        }
    }
}