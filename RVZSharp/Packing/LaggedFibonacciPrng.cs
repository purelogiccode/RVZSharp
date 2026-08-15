namespace RVZSharp.Packing;

/// <summary>
/// The Lagged Fibonacci generator (xor, j = 32, k = 521) used by the RVZ packing scheme to
/// reproduce GameCube/Wii pseudorandom padding data. Matches Dolphin's
/// LaggedFibonacciGenerator on little-endian systems (which is what the RVZ format assumes):
/// words are stored big-endian in the file, and each output word contributes the four bytes
/// (w &gt;&gt; 24, w &gt;&gt; 18, w &gt;&gt; 8, w) — note the shift by 18, not 16.
/// </summary>
internal sealed class LaggedFibonacciPrng
{
    public const int SeedWords = 17;
    public const int SeedSize = SeedWords * 4;
    public const int BufferWords = 521;
    public const int BufferSize = BufferWords * 4;
    private const int J = 32;

    private readonly uint[] _buffer = new uint[BufferWords];
    private int _bytePosition;

    public void SetSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException($"Seed must be {SeedSize} bytes.", nameof(seed));
        }

        for (var i = 0; i < SeedWords; i++)
        {
            _buffer[i] = (uint)((seed[i * 4] << 24) | (seed[i * 4 + 1] << 16) |
                                (seed[i * 4 + 2] << 8) | seed[i * 4 + 3]);
        }

        for (var i = SeedWords; i < BufferWords; i++)
        {
            _buffer[i] = (_buffer[i - 17] << 23) ^ (_buffer[i - 16] >> 9) ^ _buffer[i - 1];
        }

        for (var i = 0; i < 4; i++)
        {
            Advance();
        }

        _bytePosition = 0;
    }

    /// <summary>Advances the state without producing output (used for the offset % 0x8000 skip).</summary>
    public void Forward(int byteCount)
    {
        var remaining = byteCount;
        while (remaining > 0)
        {
            var available = BufferSize - _bytePosition;
            var take = Math.Min(remaining, available);
            _bytePosition += take;
            remaining -= take;
            if (_bytePosition == BufferSize)
            {
                Advance();
                _bytePosition = 0;
            }
        }
    }

    /// <summary>Writes the next <paramref name="count"/> bytes of PRNG output.</summary>
    public void GetBytes(Span<byte> output, int count)
    {
        var written = 0;
        while (written < count)
        {
            if (_bytePosition == BufferSize)
            {
                Advance();
                _bytePosition = 0;
            }

            var take = Math.Min(count - written, BufferSize - _bytePosition);
            for (var i = 0; i < take; i++)
            {
                output[written + i] = GetByteAt(_bytePosition + i);
            }

            _bytePosition += take;
            written += take;
        }
    }

    private byte GetByteAt(int position)
    {
        var word = _buffer[position >> 2];
        return (position & 3) switch
        {
            0 => (byte)(word >> 24),
            1 => (byte)(word >> 18), // NB: 18, not 16
            2 => (byte)(word >> 8),
            _ => (byte)word
        };
    }

    private void Advance()
    {
        for (var i = 0; i < J; i++)
        {
            _buffer[i] ^= _buffer[i + BufferWords - J];
        }

        for (var i = J; i < BufferWords; i++)
        {
            _buffer[i] ^= _buffer[i - J];
        }
    }
}
